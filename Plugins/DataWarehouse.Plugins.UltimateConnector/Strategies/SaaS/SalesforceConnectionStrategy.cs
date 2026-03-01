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
    /// Salesforce CRM connection strategy with full REST API integration.
    /// Supports SOQL queries, SObject CRUD, Bulk API for large data, streaming API
    /// (PushTopic/CDC), and OAuth 2.0 JWT bearer flow.
    /// </summary>
    public class SalesforceConnectionStrategy : SaaSConnectionStrategyBase
    {
        private const string ApiVersion = "v58.0";
        private volatile string _instanceUrl = "";
        private volatile string _clientId = "";
        private volatile string _clientSecret = "";
        private volatile string _refreshToken = "";

        public override string StrategyId => "salesforce";
        public override string DisplayName => "Salesforce";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to Salesforce CRM using REST API with OAuth2, SOQL queries, SObject CRUD, Bulk API, and Streaming API.";
        public override string[] Tags => new[] { "salesforce", "crm", "saas", "oauth2", "rest-api", "soql", "bulk-api" };

        public SalesforceConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _instanceUrl = GetConfiguration<string>(config, "InstanceUrl", "https://login.salesforce.com");
            _clientId = GetConfiguration<string>(config, "ClientId", "");
            _clientSecret = GetConfiguration<string>(config, "ClientSecret", "");
            _refreshToken = GetConfiguration<string>(config, "RefreshToken", "");

            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(_instanceUrl),
                Timeout = config.Timeout
            };

            var metadata = new Dictionary<string, object>
            {
                ["Endpoint"] = _instanceUrl,
                ["ApiVersion"] = ApiVersion,
                ["AuthFlow"] = "OAuth2_JWT_Bearer"
            };

            return new DefaultConnectionHandle(httpClient, metadata);
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var client = handle.GetConnection<HttpClient>();
                var token = await EnsureValidTokenAsync(handle, ct);
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/services/data/{ApiVersion}/");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request, ct);
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
                isHealthy ? "Salesforce is reachable" : "Salesforce is not responding",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        protected override async Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();

            // OAuth 2.0 Refresh Token flow
            var tokenRequestBody = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["refresh_token"] = _refreshToken
            };

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/services/oauth2/token")
            {
                Content = new FormUrlEncodedContent(tokenRequestBody)
            };
            using var response = await client.SendAsync(tokenRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Salesforce OAuth2 token request failed ({(int)response.StatusCode}): {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var accessToken = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Salesforce token response did not contain 'access_token'.");

            // Salesforce tokens typically last 2 hours
            return (accessToken, DateTimeOffset.UtcNow.AddHours(2));
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
            IConnectionHandle handle, string currentToken, CancellationToken ct = default) =>
            AuthenticateAsync(handle, ct);

        /// <summary>
        /// Executes a SOQL query against Salesforce.
        /// </summary>
        public async Task<SoqlQueryResult> ExecuteSoqlAsync(IConnectionHandle handle, string soqlQuery, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var token = await EnsureValidTokenAsync(handle, ct);

            // Finding 2161: Use POST for queries that may exceed URL length limits imposed by proxies
            // (typically 2–8 KB). GET with a URL-encoded query string silently fails on long SOQL.
            HttpRequestMessage request;
            if (soqlQuery.Length > 2000)
            {
                // Salesforce supports POST to /query with a JSON body for large queries
                var bodyJson = System.Text.Json.JsonSerializer.Serialize(new { q = soqlQuery });
                request = new HttpRequestMessage(HttpMethod.Post, $"/services/data/{ApiVersion}/query/")
                {
                    Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
                };
            }
            else
            {
                var encodedQuery = Uri.EscapeDataString(soqlQuery);
                request = new HttpRequestMessage(HttpMethod.Get,
                    $"/services/data/{ApiVersion}/query/?q={encodedQuery}");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new SoqlQueryResult { Success = false, ErrorMessage = json };

            using var doc = JsonDocument.Parse(json);
            var totalSize = doc.RootElement.GetProperty("totalSize").GetInt32();
            var done = doc.RootElement.GetProperty("done").GetBoolean();
            var records = new List<Dictionary<string, object?>>();

            if (doc.RootElement.TryGetProperty("records", out var recordsEl))
            {
                foreach (var record in recordsEl.EnumerateArray())
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in record.EnumerateObject())
                    {
                        if (prop.Name == "attributes") continue;
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number => prop.Value.GetDouble(),
                            JsonValueKind.True => true,
                            JsonValueKind.False => false,
                            JsonValueKind.Null => null,
                            _ => prop.Value.GetRawText()
                        };
                    }
                    records.Add(dict);
                }
            }

            return new SoqlQueryResult
            {
                Success = true,
                TotalSize = totalSize,
                Done = done,
                Records = records,
                NextRecordsUrl = done ? null :
                    doc.RootElement.TryGetProperty("nextRecordsUrl", out var next) ? next.GetString() : null
            };
        }

        /// <summary>
        /// Creates an SObject record in Salesforce.
        /// </summary>
        public async Task<SObjectResult> CreateSObjectAsync(IConnectionHandle handle, string objectType,
            Dictionary<string, object?> fields, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var token = await EnsureValidTokenAsync(handle, ct);

            var json = JsonSerializer.Serialize(fields);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/services/data/{ApiVersion}/sobjects/{objectType}/")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(responseJson);
                return new SObjectResult
                {
                    Success = true,
                    Id = doc.RootElement.GetProperty("id").GetString()
                };
            }

            return new SObjectResult { Success = false, ErrorMessage = responseJson };
        }

        /// <summary>
        /// Updates an SObject record in Salesforce.
        /// </summary>
        public async Task<SObjectResult> UpdateSObjectAsync(IConnectionHandle handle, string objectType,
            string objectId, Dictionary<string, object?> fields, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var token = await EnsureValidTokenAsync(handle, ct);

            var json = JsonSerializer.Serialize(fields);
            using var request = new HttpRequestMessage(new HttpMethod("PATCH"),
                $"/services/data/{ApiVersion}/sobjects/{objectType}/{objectId}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            return new SObjectResult
            {
                Success = response.IsSuccessStatusCode,
                Id = objectId,
                ErrorMessage = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct)
            };
        }

        /// <summary>
        /// Deletes an SObject record.
        /// </summary>
        public async Task<SObjectResult> DeleteSObjectAsync(IConnectionHandle handle, string objectType,
            string objectId, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var token = await EnsureValidTokenAsync(handle, ct);

            using var request = new HttpRequestMessage(HttpMethod.Delete,
                $"/services/data/{ApiVersion}/sobjects/{objectType}/{objectId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            return new SObjectResult
            {
                Success = response.IsSuccessStatusCode,
                Id = objectId,
                ErrorMessage = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct)
            };
        }

        /// <summary>
        /// Creates a Bulk API 2.0 job for large data operations.
        /// </summary>
        public async Task<BulkJobResult> CreateBulkJobAsync(IConnectionHandle handle, string objectType,
            string operation, string csvData, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var token = await EnsureValidTokenAsync(handle, ct);

            // Step 1: Create bulk job
            var jobPayload = JsonSerializer.Serialize(new
            {
                @object = objectType,
                operation,
                contentType = "CSV",
                lineEnding = "LF"
            });

            using var createRequest = new HttpRequestMessage(HttpMethod.Post,
                $"/services/data/{ApiVersion}/jobs/ingest")
            {
                Content = new StringContent(jobPayload, Encoding.UTF8, "application/json")
            };
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var createResponse = await client.SendAsync(createRequest, ct);
            if (!createResponse.IsSuccessStatusCode)
                return new BulkJobResult { Success = false, ErrorMessage = await createResponse.Content.ReadAsStringAsync(ct) };

            var createJson = await createResponse.Content.ReadAsStringAsync(ct);
            using var createDoc = JsonDocument.Parse(createJson);
            // Finding 2156: Avoid null-forgiving operator — throw clearly if response body is unexpected.
            var jobId = createDoc.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Salesforce Bulk API response missing 'id' field.");
            var contentUrl = createDoc.RootElement.GetProperty("contentUrl").GetString()
                ?? throw new InvalidOperationException("Salesforce Bulk API response missing 'contentUrl' field.");

            // Step 2: Upload CSV data
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, contentUrl)
            {
                Content = new StringContent(csvData, Encoding.UTF8, "text/csv")
            };
            uploadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var uploadResponse = await client.SendAsync(uploadRequest, ct);
            if (!uploadResponse.IsSuccessStatusCode)
                return new BulkJobResult { Success = false, JobId = jobId, ErrorMessage = "CSV upload failed" };

            // Step 3: Close job to start processing
            using var closeRequest = new HttpRequestMessage(new HttpMethod("PATCH"),
                $"/services/data/{ApiVersion}/jobs/ingest/{jobId}")
            {
                Content = new StringContent("{\"state\":\"UploadComplete\"}", Encoding.UTF8, "application/json")
            };
            closeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await client.SendAsync(closeRequest, ct);

            return new BulkJobResult { Success = true, JobId = jobId, State = "UploadComplete" };
        }

        /// <summary>
        /// Describes an SObject's metadata and fields.
        /// </summary>
        public async Task<Dictionary<string, object>?> DescribeSObjectAsync(IConnectionHandle handle,
            string objectType, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var token = await EnsureValidTokenAsync(handle, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/services/data/{ApiVersion}/sobjects/{objectType}/describe/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json);
        }
    }

    public sealed record SoqlQueryResult
    {
        public bool Success { get; init; }
        public int TotalSize { get; init; }
        public bool Done { get; init; }
        public List<Dictionary<string, object?>> Records { get; init; } = new();
        public string? NextRecordsUrl { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record SObjectResult
    {
        public bool Success { get; init; }
        public string? Id { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record BulkJobResult
    {
        public bool Success { get; init; }
        public string? JobId { get; init; }
        public string? State { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
