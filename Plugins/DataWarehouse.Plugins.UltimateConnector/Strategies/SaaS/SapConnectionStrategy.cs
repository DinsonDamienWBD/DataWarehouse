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
    /// SAP ERP connection strategy with OData API integration.
    /// Supports RFC/BAPI calls via OData, SAP table read/write, IDOC processing,
    /// and CSRF token management for write operations.
    /// </summary>
    public class SapConnectionStrategy : SaaSConnectionStrategyBase
    {
        private string _host = "";
        private string _client = "100";
        private string _csrfToken = "";

        public override string StrategyId => "sap";
        public override string DisplayName => "SAP";
        public override ConnectorCategory Category => ConnectorCategory.SaaS;
        public override ConnectionStrategyCapabilities Capabilities => new();
        public override string SemanticDescription => "Connects to SAP ERP using OData REST API with CSRF tokens, table read/write, and IDOC processing.";
        public override string[] Tags => new[] { "sap", "erp", "enterprise", "saas", "odata", "bapi", "idoc" };

        public SapConnectionStrategy(ILogger? logger = null) : base(logger) { }

        protected override async Task<IConnectionHandle> ConnectCoreAsync(ConnectionConfig config, CancellationToken ct)
        {
            _host = GetConfiguration<string>(config, "Host", "");
            _client = GetConfiguration<string>(config, "SapClient", "100");
            var username = GetConfiguration<string>(config, "Username", "");
            var password = GetConfiguration<string>(config, "Password", "");

            var endpoint = _host.Contains("://") ? _host : $"https://{_host}";
            var httpClient = new HttpClient
            {
                BaseAddress = new Uri(endpoint),
                Timeout = config.Timeout
            };

            if (!string.IsNullOrEmpty(username))
            {
                var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
            }
            httpClient.DefaultRequestHeaders.Add("sap-client", _client);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return new DefaultConnectionHandle(httpClient, new Dictionary<string, object>
            {
                ["Host"] = _host,
                ["Endpoint"] = endpoint,
                ["SapClient"] = _client
            });
        }

        protected override async Task<bool> TestCoreAsync(IConnectionHandle handle, CancellationToken ct)
        {
            try
            {
                var response = await handle.GetConnection<HttpClient>()
                    .GetAsync("/sap/opu/odata/sap/", ct);
                return response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable;
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
                isHealthy ? "SAP is reachable" : "SAP is not responding",
                sw.Elapsed, DateTimeOffset.UtcNow);
        }

        protected override Task<(string Token, DateTimeOffset Expiry)> AuthenticateAsync(
            IConnectionHandle handle, CancellationToken ct = default) =>
            Task.FromResult((Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddHours(8)));

        protected override Task<(string Token, DateTimeOffset Expiry)> RefreshTokenAsync(
            IConnectionHandle handle, string currentToken, CancellationToken ct = default) =>
            AuthenticateAsync(handle, ct);

        /// <summary>
        /// Fetches a CSRF token required for write operations.
        /// </summary>
        public async Task<string> FetchCsrfTokenAsync(IConnectionHandle handle, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            using var request = new HttpRequestMessage(HttpMethod.Head, "/sap/opu/odata/sap/");
            request.Headers.Add("X-CSRF-Token", "Fetch");

            var response = await client.SendAsync(request, ct);
            if (response.Headers.TryGetValues("x-csrf-token", out var tokens))
            {
                _csrfToken = tokens.FirstOrDefault() ?? "";
            }
            return _csrfToken;
        }

        /// <summary>
        /// Reads data from an SAP OData entity set.
        /// </summary>
        public async Task<SapODataResult> ReadEntitySetAsync(IConnectionHandle handle, string servicePath,
            string entitySet, string? filter = null, string? select = null, int? top = null,
            int? skip = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var queryParams = new List<string> { "$format=json" };

            if (!string.IsNullOrEmpty(filter))
                queryParams.Add($"$filter={Uri.EscapeDataString(filter)}");
            if (!string.IsNullOrEmpty(select))
                queryParams.Add($"$select={select}");
            if (top.HasValue)
                queryParams.Add($"$top={top.Value}");
            if (skip.HasValue)
                queryParams.Add($"$skip={skip.Value}");

            var url = $"/sap/opu/odata/sap/{servicePath}/{entitySet}?{string.Join("&", queryParams)}";
            var response = await client.GetAsync(url, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return new SapODataResult { Success = false, ErrorMessage = json };

            using var doc = JsonDocument.Parse(json);
            var records = new List<Dictionary<string, object?>>();

            if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("results", out var results))
            {
                foreach (var record in results.EnumerateArray())
                {
                    var dict = new Dictionary<string, object?>();
                    foreach (var prop in record.EnumerateObject())
                    {
                        if (prop.Name == "__metadata") continue;
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

            return new SapODataResult { Success = true, Records = records };
        }

        /// <summary>
        /// Creates a record via SAP OData service.
        /// </summary>
        public async Task<SapODataResult> CreateEntityAsync(IConnectionHandle handle, string servicePath,
            string entitySet, Dictionary<string, object?> fields, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_csrfToken))
                await FetchCsrfTokenAsync(handle, ct);

            var client = handle.GetConnection<HttpClient>();
            var json = JsonSerializer.Serialize(fields);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/sap/opu/odata/sap/{servicePath}/{entitySet}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-CSRF-Token", _csrfToken);

            var response = await client.SendAsync(request, ct);
            return new SapODataResult
            {
                Success = response.IsSuccessStatusCode,
                ErrorMessage = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct)
            };
        }

        /// <summary>
        /// Calls an SAP RFC/BAPI function via OData function import.
        /// </summary>
        public async Task<SapODataResult> CallFunctionImportAsync(IConnectionHandle handle, string servicePath,
            string functionName, Dictionary<string, string>? parameters = null, CancellationToken ct = default)
        {
            var client = handle.GetConnection<HttpClient>();
            var paramString = parameters != null && parameters.Count > 0
                ? "?" + string.Join("&", parameters.Select(p => $"{p.Key}='{Uri.EscapeDataString(p.Value)}'"))
                : "";

            var url = $"/sap/opu/odata/sap/{servicePath}/{functionName}{paramString}&$format=json";
            var response = await client.GetAsync(url, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            return new SapODataResult
            {
                Success = response.IsSuccessStatusCode,
                RawResponse = responseJson,
                ErrorMessage = response.IsSuccessStatusCode ? null : responseJson
            };
        }

        /// <summary>
        /// Sends an IDOC to SAP for processing.
        /// </summary>
        public async Task<SapIdocResult> SendIdocAsync(IConnectionHandle handle, string idocType,
            string xmlPayload, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_csrfToken))
                await FetchCsrfTokenAsync(handle, ct);

            var client = handle.GetConnection<HttpClient>();
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/sap/bc/srt/idoc?sap-client={_client}")
            {
                Content = new StringContent(xmlPayload, Encoding.UTF8, "application/xml")
            };
            request.Headers.Add("X-CSRF-Token", _csrfToken);

            var response = await client.SendAsync(request, ct);
            return new SapIdocResult
            {
                Success = response.IsSuccessStatusCode,
                IdocType = idocType,
                StatusCode = (int)response.StatusCode,
                ErrorMessage = response.IsSuccessStatusCode ? null : await response.Content.ReadAsStringAsync(ct)
            };
        }
    }

    public sealed record SapODataResult
    {
        public bool Success { get; init; }
        public List<Dictionary<string, object?>> Records { get; init; } = new();
        public string? RawResponse { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public sealed record SapIdocResult
    {
        public bool Success { get; init; }
        public string? IdocType { get; init; }
        public int StatusCode { get; init; }
        public string? ErrorMessage { get; init; }
    }
}
