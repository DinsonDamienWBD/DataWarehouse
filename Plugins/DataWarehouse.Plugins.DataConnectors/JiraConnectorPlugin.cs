using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Jira SaaS connector plugin.
/// Provides full Jira REST API v3 integration with issue management, JQL queries,
/// project operations, workflow transitions, attachments, and comments.
/// Implements proper authentication (API token, OAuth), rate limiting, and pagination.
/// </summary>
public class JiraConnectorPlugin : SaaSConnectorPluginBase
{
    private HttpClient? _httpClient;
    private string? _baseUrl;
    private string? _email;
    private string? _apiToken;
    private string? _oauthAccessToken;
    private string? _oauthRefreshToken;
    private string? _clientId;
    private string? _clientSecret;
    private AuthenticationType _authType;
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;
    private const int MinRequestIntervalMs = 100; // Rate limiting: max 10 requests/second

    /// <summary>
    /// Authentication type used by the connector.
    /// </summary>
    private enum AuthenticationType
    {
        /// <summary>API token authentication (email + token).</summary>
        ApiToken,
        /// <summary>OAuth 2.0 authentication.</summary>
        OAuth
    }

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.jira";

    /// <inheritdoc />
    public override string Name => "Jira Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "jira";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            var props = (IReadOnlyDictionary<string, string?>)config.Properties;
            _httpClient = new HttpClient();
            _baseUrl = props.GetValueOrDefault("BaseUrl", "") ?? "";

            if (string.IsNullOrWhiteSpace(_baseUrl))
            {
                return new ConnectionResult(false, "BaseUrl is required (e.g., https://your-domain.atlassian.net)", null);
            }

            // Remove trailing slash
            _baseUrl = _baseUrl.TrimEnd('/');

            // Determine authentication type
            _email = props.GetValueOrDefault("Email", "");
            _apiToken = props.GetValueOrDefault("ApiToken", "");
            _clientId = props.GetValueOrDefault("ClientId", "");
            _clientSecret = props.GetValueOrDefault("ClientSecret", "");

            if (!string.IsNullOrWhiteSpace(_email) && !string.IsNullOrWhiteSpace(_apiToken))
            {
                _authType = AuthenticationType.ApiToken;
                AccessToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_email}:{_apiToken}")) ?? "";
                TokenExpiry = DateTimeOffset.MaxValue; // API tokens don't expire
            }
            else if (!string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret))
            {
                _authType = AuthenticationType.OAuth;
                AccessToken = await AuthenticateAsync(config) ?? "";
            }
            else
            {
                return new ConnectionResult(false,
                    "Either Email+ApiToken or ClientId+ClientSecret+AuthCode are required", null);
            }

            // Test connection by fetching server info
            var serverInfo = await GetServerInfoAsync(ct);
            serverInfo["BaseUrl"] = _baseUrl;
            serverInfo["AuthType"] = _authType.ToString();

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Jira connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<string> AuthenticateAsync(ConnectorConfig config)
    {
        if (_authType == AuthenticationType.ApiToken)
        {
            // API token is already set in EstablishConnectionAsync
            return AccessToken!;
        }

        // OAuth 2.0 flow
        var props = (IReadOnlyDictionary<string, string?>)config.Properties;
        var authCode = props.GetValueOrDefault("AuthCode", "");
 var redirectUri = props.GetValueOrDefault("RedirectUri", "");

        if (string.IsNullOrWhiteSpace(authCode))
        {
            throw new InvalidOperationException("AuthCode is required for OAuth authentication");
        }

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
            ["code"] = authCode,
            ["redirect_uri"] = redirectUri
        });

        var response = await _httpClient!.PostAsync("https://auth.atlassian.com/oauth/token", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _oauthAccessToken = root.GetProperty("access_token").GetString() ?? "";
        _oauthRefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        var expiresIn = root.GetProperty("expires_in").GetInt32();
        TokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        return _oauthAccessToken!;
    }

    /// <inheritdoc />
    protected override async Task RefreshTokenAsync()
    {
        if (_authType == AuthenticationType.ApiToken)
        {
            // API tokens don't expire
            return;
        }

        if (string.IsNullOrEmpty(_oauthRefreshToken))
        {
            throw new InvalidOperationException("No refresh token available for OAuth");
        }

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
            ["refresh_token"] = _oauthRefreshToken
        });

        var response = await _httpClient!.PostAsync("https://auth.atlassian.com/oauth/token", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _oauthAccessToken = root.GetProperty("access_token").GetString() ?? "";
        AccessToken = _oauthAccessToken ?? "";

        var expiresIn = root.GetProperty("expires_in").GetInt32();
        TokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _httpClient?.Dispose();
            _httpClient = null;
            AccessToken = "";
            _baseUrl = null;
            _email = null;
            _apiToken = null;
            _oauthAccessToken = "";
            _oauthRefreshToken = null;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<bool> PingAsync()
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken)) return false;

        try
        {
            await EnsureValidTokenAsync();
            var request = CreateAuthenticatedRequest(HttpMethod.Get, "/rest/api/3/serverInfo");
            var response = await SendWithRateLimitAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to Jira");

        await EnsureValidTokenAsync();

        // Fetch all projects
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/rest/api/3/project");
        var response = await SendWithRateLimitAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var projects = doc.RootElement.EnumerateArray().Select(p => p.GetProperty("key").GetString()!).ToList();

        return new DataSchema(
            Name: "jira",
            Fields: new[]
            {
                new DataSchemaField("id", "string", false, null, null),
                new DataSchemaField("key", "string", false, null, null),
                new DataSchemaField("summary", "string", false, null, null),
                new DataSchemaField("description", "string", true, null, null),
                new DataSchemaField("status", "string", false, null, null),
                new DataSchemaField("assignee", "string", true, null, null),
                new DataSchemaField("reporter", "string", false, null, null),
                new DataSchemaField("priority", "string", true, null, null),
                new DataSchemaField("created", "datetime", false, null, null),
                new DataSchemaField("updated", "datetime", false, null, null),
                new DataSchemaField("project", "string", false, null, null),
                new DataSchemaField("issueType", "string", false, null, null)
            },
            PrimaryKeys: new[] { "id" },
            Metadata: new Dictionary<string, object>
            {
                ["ProjectCount"] = projects.Count,
                ["Projects"] = projects.ToArray(),
                ["ApiVersion"] = "3"
            }
        );
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to Jira");

        await EnsureValidTokenAsync();

        // Build JQL query
        var jql = query.Filter ?? "ORDER BY created DESC";
        var fields = query.Fields ?? new[] { "summary", "status", "assignee", "reporter", "priority", "created", "updated" };
        var maxResults = query.Limit ?? 100;

        var startAt = 0;
        long position = 0;
        bool hasMore = true;

        while (hasMore && !ct.IsCancellationRequested)
        {
            var url = $"/rest/api/3/search?jql={Uri.EscapeDataString(jql)}&fields={string.Join(",", fields)}&startAt={startAt}&maxResults={maxResults}";
            var request = CreateAuthenticatedRequest(HttpMethod.Get, url);
            var response = await SendWithRateLimitAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var issues = root.GetProperty("issues");
            foreach (var issue in issues.EnumerateArray())
            {
                if (ct.IsCancellationRequested) yield break;

                var values = new Dictionary<string, object?>
                {
                    ["id"] = issue.GetProperty("id").GetString(),
                    ["key"] = issue.GetProperty("key").GetString(),
                    ["self"] = issue.GetProperty("self").GetString()
                };

                var fieldsObj = issue.GetProperty("fields");
                foreach (var field in fieldsObj.EnumerateObject())
                {
                    values[field.Name] = field.Value.ValueKind == JsonValueKind.Null
                        ? null
                        : ExtractFieldValue(field.Value);
                }

                yield return new DataRecord(values, position++, DateTimeOffset.UtcNow);
            }

            var total = root.GetProperty("total").GetInt32();
            startAt += maxResults;
            hasMore = startAt < total;
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to Jira");

        await EnsureValidTokenAsync();

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var operation = options.TargetTable?.ToLowerInvariant() ?? "create";

        await foreach (var record in records.WithCancellation(ct))
        {
            try
            {
                string? issueKey = null;

                switch (operation)
                {
                    case "create":
                        issueKey = await CreateIssueAsync(record, ct);
                        break;
                    case "update":
                        issueKey = await UpdateIssueAsync(record, ct);
                        break;
                    case "delete":
                        await DeleteIssueAsync(record, ct);
                        issueKey = record.Values.GetValueOrDefault("key")?.ToString();
                        break;
                    case "transition":
                        issueKey = await TransitionIssueAsync(record, ct);
                        break;
                    case "comment":
                        issueKey = await AddCommentAsync(record, ct);
                        break;
                    default:
                        throw new ArgumentException($"Unsupported operation: {operation}");
                }

                written++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Record at position {record.Position}: {ex.Message}");
            }
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    #region Jira-Specific Operations

    /// <summary>
    /// Gets Jira server information.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary containing server information.</returns>
    private async Task<Dictionary<string, object>> GetServerInfoAsync(CancellationToken ct)
    {
        var request = CreateAuthenticatedRequest(HttpMethod.Get, "/rest/api/3/serverInfo");
        var response = await SendWithRateLimitAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new Dictionary<string, object>
        {
            ["ServerTitle"] = root.TryGetProperty("serverTitle", out var title) ? title.GetString()! : "Unknown",
            ["Version"] = root.TryGetProperty("version", out var version) ? version.GetString()! : "Unknown",
            ["DeploymentType"] = root.TryGetProperty("deploymentType", out var dt) ? dt.GetString()! : "Unknown"
        };
    }

    /// <summary>
    /// Creates a new Jira issue.
    /// </summary>
    /// <param name="record">Record containing issue data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Key of the created issue.</returns>
    private async Task<string> CreateIssueAsync(DataRecord record, CancellationToken ct)
    {
        var project = record.Values.GetValueOrDefault("project")?.ToString()
            ?? throw new ArgumentException("Project is required for issue creation");
        var issueType = record.Values.GetValueOrDefault("issueType")?.ToString()
            ?? throw new ArgumentException("IssueType is required for issue creation");
        var summary = record.Values.GetValueOrDefault("summary")?.ToString()
            ?? throw new ArgumentException("Summary is required for issue creation");

        var issueData = new
        {
            fields = new Dictionary<string, object>
            {
                ["project"] = new { key = project },
                ["summary"] = summary,
                ["issuetype"] = new { name = issueType },
                ["description"] = CreateDescription(record.Values.GetValueOrDefault("description")?.ToString())
            }
        };

        // Add optional fields
        if (record.Values.TryGetValue("assignee", out var assignee) && assignee != null)
        {
            issueData.fields["assignee"] = new { accountId = assignee.ToString() };
        }
        if (record.Values.TryGetValue("priority", out var priority) && priority != null)
        {
            issueData.fields["priority"] = new { name = priority.ToString() };
        }

        var jsonContent = JsonSerializer.Serialize(issueData);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var request = CreateAuthenticatedRequest(HttpMethod.Post, "/rest/api/3/issue");
        request.Content = content;

        var response = await SendWithRateLimitAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("key").GetString()!;
    }

    /// <summary>
    /// Updates an existing Jira issue.
    /// </summary>
    /// <param name="record">Record containing issue data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Key of the updated issue.</returns>
    private async Task<string> UpdateIssueAsync(DataRecord record, CancellationToken ct)
    {
        var issueKey = record.Values.GetValueOrDefault("key")?.ToString()
            ?? record.Values.GetValueOrDefault("id")?.ToString()
            ?? throw new ArgumentException("Issue key or id is required for update");

        var fields = new Dictionary<string, object>();

        if (record.Values.TryGetValue("summary", out var summary) && summary != null)
        {
            fields["summary"] = summary.ToString()!;
        }
        if (record.Values.TryGetValue("description", out var description) && description != null)
        {
            fields["description"] = CreateDescription(description.ToString());
        }
        if (record.Values.TryGetValue("assignee", out var assignee) && assignee != null)
        {
            fields["assignee"] = new { accountId = assignee.ToString() };
        }
        if (record.Values.TryGetValue("priority", out var priority) && priority != null)
        {
            fields["priority"] = new { name = priority.ToString() };
        }

        var updateData = new { fields };
        var jsonContent = JsonSerializer.Serialize(updateData);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var request = CreateAuthenticatedRequest(HttpMethod.Put, $"/rest/api/3/issue/{issueKey}");
        request.Content = content;

        var response = await SendWithRateLimitAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return issueKey;
    }

    /// <summary>
    /// Deletes a Jira issue.
    /// </summary>
    /// <param name="record">Record containing issue key.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task DeleteIssueAsync(DataRecord record, CancellationToken ct)
    {
        var issueKey = record.Values.GetValueOrDefault("key")?.ToString()
            ?? record.Values.GetValueOrDefault("id")?.ToString()
            ?? throw new ArgumentException("Issue key or id is required for deletion");

        var request = CreateAuthenticatedRequest(HttpMethod.Delete, $"/rest/api/3/issue/{issueKey}");
        var response = await SendWithRateLimitAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Transitions a Jira issue to a new status.
    /// </summary>
    /// <param name="record">Record containing issue key and transition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Key of the transitioned issue.</returns>
    private async Task<string> TransitionIssueAsync(DataRecord record, CancellationToken ct)
    {
        var issueKey = record.Values.GetValueOrDefault("key")?.ToString()
            ?? record.Values.GetValueOrDefault("id")?.ToString()
            ?? throw new ArgumentException("Issue key or id is required for transition");

        var transitionId = record.Values.GetValueOrDefault("transitionId")?.ToString()
            ?? throw new ArgumentException("TransitionId is required for transition");

        var transitionData = new
        {
            transition = new { id = transitionId }
        };

        var jsonContent = JsonSerializer.Serialize(transitionData);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var request = CreateAuthenticatedRequest(HttpMethod.Post, $"/rest/api/3/issue/{issueKey}/transitions");
        request.Content = content;

        var response = await SendWithRateLimitAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return issueKey;
    }

    /// <summary>
    /// Adds a comment to a Jira issue.
    /// </summary>
    /// <param name="record">Record containing issue key and comment body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Key of the issue.</returns>
    private async Task<string> AddCommentAsync(DataRecord record, CancellationToken ct)
    {
        var issueKey = record.Values.GetValueOrDefault("key")?.ToString()
            ?? record.Values.GetValueOrDefault("id")?.ToString()
            ?? throw new ArgumentException("Issue key or id is required for comment");

        var commentBody = record.Values.GetValueOrDefault("body")?.ToString()
            ?? record.Values.GetValueOrDefault("comment")?.ToString()
            ?? throw new ArgumentException("Comment body is required");

        var commentData = new
        {
            body = CreateDescription(commentBody)
        };

        var jsonContent = JsonSerializer.Serialize(commentData);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var request = CreateAuthenticatedRequest(HttpMethod.Post, $"/rest/api/3/issue/{issueKey}/comment");
        request.Content = content;

        var response = await SendWithRateLimitAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return issueKey;
    }

    /// <summary>
    /// Creates a description object in Atlassian Document Format (ADF).
    /// </summary>
    /// <param name="text">Plain text description.</param>
    /// <returns>ADF-formatted description object.</returns>
    private static object CreateDescription(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new
            {
                type = "doc",
                version = 1,
                content = Array.Empty<object>()
            };
        }

        return new
        {
            type = "doc",
            version = 1,
            content = new[]
            {
                new
                {
                    type = "paragraph",
                    content = new[]
                    {
                        new { type = "text", text }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Extracts a field value from a JSON element, handling complex types.
    /// </summary>
    /// <param name="element">JSON element to extract.</param>
    /// <returns>Extracted value as object.</returns>
    private static object? ExtractFieldValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => ExtractObjectValue(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ExtractFieldValue).ToArray(),
            _ => element.ToString()
        };
    }

    /// <summary>
    /// Extracts an object value, handling common Jira object types.
    /// </summary>
    /// <param name="element">JSON object element.</param>
    /// <returns>Extracted value.</returns>
    private static object? ExtractObjectValue(JsonElement element)
    {
        // Check for common Jira object patterns
        if (element.TryGetProperty("name", out var name))
        {
            return name.GetString();
        }
        if (element.TryGetProperty("displayName", out var displayName))
        {
            return displayName.GetString();
        }
        if (element.TryGetProperty("value", out var value))
        {
            return value.GetString();
        }

        // Return full object as JSON string
        return element.ToString();
    }

    #endregion

    #region HTTP Helpers

    /// <summary>
    /// Creates an authenticated HTTP request with proper headers.
    /// </summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">API path (relative to base URL).</param>
    /// <returns>Configured HTTP request message.</returns>
    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _baseUrl + path);

        if (_authType == AuthenticationType.ApiToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", AccessToken);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _oauthAccessToken);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    /// <summary>
    /// Sends an HTTP request with rate limiting enforcement.
    /// Implements simple rate limiting to avoid hitting Jira API limits (10 req/sec).
    /// </summary>
    /// <param name="request">HTTP request to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>HTTP response message.</returns>
    private async Task<HttpResponseMessage> SendWithRateLimitAsync(
        HttpRequestMessage request,
        CancellationToken ct = default)
    {
        await _rateLimitLock.WaitAsync(ct);
        try
        {
            var timeSinceLastRequest = DateTimeOffset.UtcNow - _lastRequestTime;
            var minInterval = TimeSpan.FromMilliseconds(MinRequestIntervalMs);

            if (timeSinceLastRequest < minInterval)
            {
                await Task.Delay(minInterval - timeSinceLastRequest, ct);
            }

            var response = await _httpClient!.SendAsync(request, ct);
            _lastRequestTime = DateTimeOffset.UtcNow;

            // Handle rate limiting responses
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                await Task.Delay(retryAfter, ct);

                // Retry the request
                var retryRequest = CloneRequest(request);
                response = await _httpClient.SendAsync(retryRequest, ct);
            }

            return response;
        }
        finally
        {
            _rateLimitLock.Release();
        }
    }

    /// <summary>
    /// Clones an HTTP request message for retry scenarios.
    /// </summary>
    /// <param name="request">Original request.</param>
    /// <returns>Cloned request.</returns>
    private HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content != null)
        {
            var content = new StringContent(
                request.Content.ReadAsStringAsync().Result,
                Encoding.UTF8,
                request.Content.Headers.ContentType?.MediaType ?? "application/json");
            clone.Content = content;
        }

        return clone;
    }

    #endregion

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}
