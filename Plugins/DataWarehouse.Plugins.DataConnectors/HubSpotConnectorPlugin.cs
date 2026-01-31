using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready HubSpot CRM connector plugin.
/// Provides comprehensive access to HubSpot CRM data via REST API v3 with OAuth 2.0 authentication.
/// Supports Contacts, Companies, Deals, and advanced features like search, batch operations, and associations.
/// </summary>
/// <remarks>
/// Features:
/// - OAuth 2.0 authentication with automatic token refresh
/// - Full CRUD operations for Contacts, Companies, and Deals
/// - CRM Search API integration for advanced filtering
/// - Batch operations for bulk data processing (up to 100 records per request)
/// - Association management between CRM objects
/// - Rate limit handling with exponential backoff (HubSpot: 100 requests/10 seconds)
/// - Comprehensive error handling and retry logic
/// - Full async/await implementation for optimal performance
///
/// Rate Limits:
/// - Standard: 100 requests per 10 seconds
/// - Batch operations: 4 requests per 10 seconds
/// - Search API: 4 requests per second
/// </remarks>
public class HubSpotConnectorPlugin : SaaSConnectorPluginBase
{
    private HttpClient? _httpClient;
    private string? _refreshToken;
    private string? _clientId;
    private string? _clientSecret;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    private DateTimeOffset _lastRequestTime = DateTimeOffset.MinValue;
    private int _requestCount = 0;
    private const int MaxRequestsPer10Seconds = 100;
    private const int BatchMaxRequestsPer10Seconds = 4;
    private const string ApiVersion = "v3";
    private const string BaseApiUrl = "https://api.hubapi.com";

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.hubspot";

    /// <inheritdoc />
    public override string Name => "HubSpot CRM Connector";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "hubspot";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <summary>
    /// Enhanced capabilities including bulk operations support.
    /// </summary>
    public override ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.Read |
        ConnectorCapabilities.Write |
        ConnectorCapabilities.Schema |
        ConnectorCapabilities.BulkOperations;

    #region Connection Management

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(BaseApiUrl),
                Timeout = config.Timeout ?? TimeSpan.FromSeconds(30)
            };

            var props = (IReadOnlyDictionary<string, string?>)config.Properties;
            _clientId = props.GetValueOrDefault("ClientId", "");
            _clientSecret = props.GetValueOrDefault("ClientSecret", "");

            if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
            {
                return new ConnectionResult(false, "ClientId and ClientSecret are required for OAuth 2.0", null);
            }

            // Check if we have an authorization code or refresh token
            var authCode = props.GetValueOrDefault("AuthorizationCode", "");
            _refreshToken = props.GetValueOrDefault("RefreshToken", "");

            if (string.IsNullOrWhiteSpace(authCode) && string.IsNullOrWhiteSpace(_refreshToken))
            {
                return new ConnectionResult(false,
                    "Either AuthorizationCode or RefreshToken is required. For OAuth flow, provide AuthorizationCode.",
                    null);
            }

            // Authenticate using OAuth 2.0
            AccessToken = await AuthenticateAsync(config) ?? "";

            var serverInfo = new Dictionary<string, object>
            {
                ["BaseUrl"] = BaseApiUrl,
                ["ApiVersion"] = ApiVersion,
                ["AuthType"] = "OAuth2",
                ["RateLimitPerSecond"] = MaxRequestsPer10Seconds / 10
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"HubSpot connection failed: {ex.Message}", null);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task<string> AuthenticateAsync(ConnectorConfig config)
    {
        var props = (IReadOnlyDictionary<string, string?>)config.Properties;
        var authCode = props.GetValueOrDefault("AuthorizationCode", "");
        var refreshToken = props.GetValueOrDefault("RefreshToken", "");
        var redirectUri = props.GetValueOrDefault("RedirectUri", "http://localhost");

        // If we have a refresh token, use it; otherwise use authorization code
        var grantType = !string.IsNullOrEmpty(refreshToken) ? "refresh_token" : "authorization_code";

        var formData = new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!
        };

        if (grantType == "refresh_token")
        {
            formData["refresh_token"] = refreshToken;
        }
        else
        {
            formData["code"] = authCode;
            formData["redirect_uri"] = redirectUri;
        }

        var content = new FormUrlEncodedContent(formData);
        var response = await _httpClient!.PostAsync("https://api.hubapi.com/oauth/v1/token", content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"HubSpot authentication failed: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _refreshToken = root.GetProperty("refresh_token").GetString();
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        TokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        return root.GetProperty("access_token").GetString()!;
    }

    /// <inheritdoc />
    protected override async Task RefreshTokenAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken))
        {
            throw new InvalidOperationException("No refresh token available for token refresh");
        }

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
            ["refresh_token"] = _refreshToken
        });

        var response = await _httpClient!.PostAsync("https://api.hubapi.com/oauth/v1/token", content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Token refresh failed: {error}");
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        AccessToken = root.GetProperty("access_token").GetString() ?? "";
        _refreshToken = root.GetProperty("refresh_token").GetString();
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
            _refreshToken = null;
            _requestCount = 0;
            _lastRequestTime = DateTimeOffset.MinValue;
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
            var request = new HttpRequestMessage(HttpMethod.Get, $"/crm/{ApiVersion}/objects/contacts?limit=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

            var response = await ExecuteWithRateLimitAsync(() => _httpClient.SendAsync(request));
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Rate Limiting

    /// <summary>
    /// Executes an HTTP request with rate limiting to comply with HubSpot's API limits.
    /// Implements exponential backoff for 429 (Too Many Requests) responses.
    /// </summary>
    /// <typeparam name="T">Return type of the function.</typeparam>
    /// <param name="action">Function to execute.</param>
    /// <param name="isBatchOperation">Whether this is a batch operation (lower rate limit).</param>
    /// <returns>Result of the function.</returns>
    private async Task<T> ExecuteWithRateLimitAsync<T>(Func<Task<T>> action, bool isBatchOperation = false)
    {
        await _rateLimitLock.WaitAsync();
        try
        {
            // Reset counter if 10 seconds have passed
            if ((DateTimeOffset.UtcNow - _lastRequestTime).TotalSeconds >= 10)
            {
                _requestCount = 0;
                _lastRequestTime = DateTimeOffset.UtcNow;
            }

            var maxRequests = isBatchOperation ? BatchMaxRequestsPer10Seconds : MaxRequestsPer10Seconds;

            // Wait if we've hit the rate limit
            if (_requestCount >= maxRequests)
            {
                var waitTime = TimeSpan.FromSeconds(10) - (DateTimeOffset.UtcNow - _lastRequestTime);
                if (waitTime > TimeSpan.Zero)
                {
                    await Task.Delay(waitTime);
                }
                _requestCount = 0;
                _lastRequestTime = DateTimeOffset.UtcNow;
            }

            _requestCount++;
        }
        finally
        {
            _rateLimitLock.Release();
        }

        // Execute with retry logic for rate limit errors
        int retryCount = 0;
        const int maxRetries = 3;

        while (true)
        {
            try
            {
                var result = await action();

                // Check if result is HttpResponseMessage and handle 429
                if (result is HttpResponseMessage response && response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (retryCount >= maxRetries)
                    {
                        throw new InvalidOperationException("Rate limit exceeded after maximum retries");
                    }

                    // Exponential backoff: 1s, 2s, 4s
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount));
                    await Task.Delay(delay);
                    retryCount++;
                    continue;
                }

                return result;
            }
            catch (Exception) when (retryCount < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount));
                await Task.Delay(delay);
                retryCount++;
            }
        }
    }

    #endregion

    #region Schema Management

    /// <inheritdoc />
    protected override async Task<DataSchema> FetchSchemaAsync()
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to HubSpot");

        await EnsureValidTokenAsync();

        // Fetch schema for Contacts object as the primary schema
        var request = new HttpRequestMessage(HttpMethod.Get, $"/crm/{ApiVersion}/properties/contacts");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient.SendAsync(request));
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("results");

        var fields = new List<DataSchemaField>();
        foreach (var prop in results.EnumerateArray())
        {
            var name = prop.GetProperty("name").GetString()!;
            var type = prop.GetProperty("type").GetString()!;
            var fieldType = prop.TryGetProperty("fieldType", out var ft) ? ft.GetString() : type;

            fields.Add(new DataSchemaField(
                Name: name,
                DataType: MapHubSpotType(type, fieldType),
                Nullable: true,
                MaxLength: null,
                Properties: new Dictionary<string, object>
                {
                    ["HubSpotType"] = type,
                    ["FieldType"] = fieldType ?? type
                }
            ));
        }

        return new DataSchema(
            Name: "contacts",
            Fields: fields.ToArray(),
            PrimaryKeys: new[] { "id" },
            Metadata: new Dictionary<string, object>
            {
                ["ObjectTypes"] = new[] { "contacts", "companies", "deals" },
                ["SupportsSearch"] = true,
                ["SupportsAssociations"] = true,
                ["SupportsBatch"] = true,
                ["MaxBatchSize"] = 100
            }
        );
    }

    /// <summary>
    /// Maps HubSpot property types to standard data types.
    /// </summary>
    /// <param name="hubspotType">HubSpot type string.</param>
    /// <param name="fieldType">HubSpot field type string.</param>
    /// <returns>Standardized data type.</returns>
    private static string MapHubSpotType(string? hubspotType, string? fieldType)
    {
        return (hubspotType?.ToLowerInvariant(), fieldType?.ToLowerInvariant()) switch
        {
            ("string", _) => "string",
            ("number", _) => "number",
            ("bool", _) or ("boolean", _) => "boolean",
            ("datetime", _) or ("date", _) => "datetime",
            ("enumeration", _) => "string",
            ("phone_number", _) => "string",
            ("", "textarea") => "string",
            _ => "string"
        };
    }

    #endregion

    #region CRUD Operations

    /// <summary>
    /// Creates a new CRM object (Contact, Company, or Deal).
    /// </summary>
    /// <param name="objectType">Type of object (contacts, companies, deals).</param>
    /// <param name="properties">Properties for the new object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created object ID and all properties.</returns>
    public async Task<(string Id, Dictionary<string, object?> Properties)> CreateObjectAsync(
        string objectType,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        await EnsureValidTokenAsync();

        var payload = new { properties };
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/crm/{ApiVersion}/objects/{objectType}")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct));
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.GetProperty("id").GetString()!;
        var props = ParseProperties(root.GetProperty("properties"));

        return (id, props);
    }

    /// <summary>
    /// Retrieves a CRM object by ID.
    /// </summary>
    /// <param name="objectType">Type of object (contacts, companies, deals).</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="properties">Specific properties to retrieve (null for all).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Object properties.</returns>
    public async Task<Dictionary<string, object?>> GetObjectAsync(
        string objectType,
        string objectId,
        string[]? properties = null,
        CancellationToken ct = default)
    {
        await EnsureValidTokenAsync();

        var url = $"/crm/{ApiVersion}/objects/{objectType}/{objectId}";
        if (properties != null && properties.Length > 0)
        {
            url += $"?properties={string.Join(",", properties)}";
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct));
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return ParseProperties(doc.RootElement.GetProperty("properties"));
    }

    /// <summary>
    /// Updates a CRM object.
    /// </summary>
    /// <param name="objectType">Type of object (contacts, companies, deals).</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="properties">Properties to update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated properties.</returns>
    public async Task<Dictionary<string, object?>> UpdateObjectAsync(
        string objectType,
        string objectId,
        Dictionary<string, object?> properties,
        CancellationToken ct = default)
    {
        await EnsureValidTokenAsync();

        var payload = new { properties };
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/crm/{ApiVersion}/objects/{objectType}/{objectId}")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct));
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return ParseProperties(doc.RootElement.GetProperty("properties"));
    }

    /// <summary>
    /// Deletes a CRM object.
    /// </summary>
    /// <param name="objectType">Type of object (contacts, companies, deals).</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteObjectAsync(
        string objectType,
        string objectId,
        CancellationToken ct = default)
    {
        await EnsureValidTokenAsync();

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/crm/{ApiVersion}/objects/{objectType}/{objectId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct));
        response.EnsureSuccessStatusCode();
    }

    #endregion

    #region Batch Operations

    /// <summary>
    /// Creates multiple CRM objects in a single batch request (up to 100).
    /// </summary>
    /// <param name="objectType">Type of object (contacts, companies, deals).</param>
    /// <param name="objects">List of objects to create with their properties.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of created object IDs and status.</returns>
    public async Task<List<BatchOperationResult>> BatchCreateAsync(
        string objectType,
        List<Dictionary<string, object?>> objects,
        CancellationToken ct = default)
    {
        if (objects.Count > 100)
            throw new ArgumentException("Batch size cannot exceed 100 objects", nameof(objects));

        await EnsureValidTokenAsync();

        var inputs = objects.Select(props => new { properties = props }).ToList();
        var payload = new { inputs };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/crm/{ApiVersion}/objects/{objectType}/batch/create")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct), isBatchOperation: true);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return ParseBatchResults(doc.RootElement);
    }

    /// <summary>
    /// Updates multiple CRM objects in a single batch request (up to 100).
    /// </summary>
    /// <param name="objectType">Type of object (contacts, companies, deals).</param>
    /// <param name="updates">List of object IDs with properties to update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of updated object results.</returns>
    public async Task<List<BatchOperationResult>> BatchUpdateAsync(
        string objectType,
        List<(string Id, Dictionary<string, object?> Properties)> updates,
        CancellationToken ct = default)
    {
        if (updates.Count > 100)
            throw new ArgumentException("Batch size cannot exceed 100 objects", nameof(updates));

        await EnsureValidTokenAsync();

        var inputs = updates.Select(u => new { id = u.Id, properties = u.Properties }).ToList();
        var payload = new { inputs };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/crm/{ApiVersion}/objects/{objectType}/batch/update")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct), isBatchOperation: true);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return ParseBatchResults(doc.RootElement);
    }

    /// <summary>
    /// Reads multiple CRM objects by ID in a single batch request (up to 100).
    /// </summary>
    /// <param name="objectType">Type of object (contacts, companies, deals).</param>
    /// <param name="ids">List of object IDs to retrieve.</param>
    /// <param name="properties">Specific properties to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of retrieved objects.</returns>
    public async Task<List<BatchOperationResult>> BatchReadAsync(
        string objectType,
        List<string> ids,
        string[]? properties = null,
        CancellationToken ct = default)
    {
        if (ids.Count > 100)
            throw new ArgumentException("Batch size cannot exceed 100 objects", nameof(ids));

        await EnsureValidTokenAsync();

        var inputs = ids.Select(id => new { id }).ToList();
        var payload = new
        {
            inputs,
            properties = properties ?? Array.Empty<string>()
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/crm/{ApiVersion}/objects/{objectType}/batch/read")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct), isBatchOperation: true);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return ParseBatchResults(doc.RootElement);
    }

    #endregion

    #region Search Operations

    /// <summary>
    /// Searches CRM objects using HubSpot's powerful CRM Search API.
    /// Supports filtering, sorting, and pagination.
    /// </summary>
    /// <param name="objectType">Type of object to search (contacts, companies, deals).</param>
    /// <param name="filters">Search filters.</param>
    /// <param name="sorts">Sort specifications.</param>
    /// <param name="properties">Properties to return.</param>
    /// <param name="limit">Maximum results to return (max 100).</param>
    /// <param name="after">Pagination cursor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Search results with pagination info.</returns>
    public async Task<SearchResult> SearchObjectsAsync(
        string objectType,
        List<SearchFilter>? filters = null,
        List<SearchSort>? sorts = null,
        string[]? properties = null,
        int limit = 100,
        string? after = null,
        CancellationToken ct = default)
    {
        await EnsureValidTokenAsync();

        var payload = new
        {
            filterGroups = filters != null && filters.Any()
                ? new[] { new { filters = filters.Select(f => new
                    {
                        propertyName = f.PropertyName,
                        @operator = f.Operator,
                        value = f.Value
                    }) } }
                : Array.Empty<object>(),
            sorts = sorts?.Select(s => new { propertyName = s.PropertyName, direction = s.Direction }).ToArray() ?? Array.Empty<object>(),
            properties = properties ?? Array.Empty<string>(),
            limit,
            after
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/crm/{ApiVersion}/objects/{objectType}/search")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct));
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var results = new List<Dictionary<string, object?>>();
        if (root.TryGetProperty("results", out var resultsArray))
        {
            foreach (var result in resultsArray.EnumerateArray())
            {
                var props = ParseProperties(result.GetProperty("properties"));
                props["id"] = result.GetProperty("id").GetString();
                results.Add(props);
            }
        }

        var paging = root.TryGetProperty("paging", out var pagingObj) && pagingObj.TryGetProperty("next", out var next)
            ? next.GetProperty("after").GetString()
            : null;

        return new SearchResult(results, paging);
    }

    #endregion

    #region Association Management

    /// <summary>
    /// Creates an association between two CRM objects.
    /// </summary>
    /// <param name="fromObjectType">Type of the source object (e.g., "contacts").</param>
    /// <param name="fromObjectId">ID of the source object.</param>
    /// <param name="toObjectType">Type of the target object (e.g., "companies").</param>
    /// <param name="toObjectId">ID of the target object.</param>
    /// <param name="associationTypeId">Association type ID (e.g., 1 for contact to company).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CreateAssociationAsync(
        string fromObjectType,
        string fromObjectId,
        string toObjectType,
        string toObjectId,
        int associationTypeId,
        CancellationToken ct = default)
    {
        await EnsureValidTokenAsync();

        var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/crm/{ApiVersion}/objects/{fromObjectType}/{fromObjectId}/associations/{toObjectType}/{toObjectId}/{associationTypeId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Retrieves associations for a CRM object.
    /// </summary>
    /// <param name="objectType">Type of object (contacts, companies, deals).</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="toObjectType">Type of associated objects to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of associated object IDs.</returns>
    public async Task<List<string>> GetAssociationsAsync(
        string objectType,
        string objectId,
        string toObjectType,
        CancellationToken ct = default)
    {
        await EnsureValidTokenAsync();

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/crm/{ApiVersion}/objects/{objectType}/{objectId}/associations/{toObjectType}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct));
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var associations = new List<string>();
        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            foreach (var assoc in results.EnumerateArray())
            {
                associations.Add(assoc.GetProperty("id").GetString()!);
            }
        }

        return associations;
    }

    /// <summary>
    /// Deletes an association between two CRM objects.
    /// </summary>
    /// <param name="fromObjectType">Type of the source object.</param>
    /// <param name="fromObjectId">ID of the source object.</param>
    /// <param name="toObjectType">Type of the target object.</param>
    /// <param name="toObjectId">ID of the target object.</param>
    /// <param name="associationTypeId">Association type ID.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DeleteAssociationAsync(
        string fromObjectType,
        string fromObjectId,
        string toObjectType,
        string toObjectId,
        int associationTypeId,
        CancellationToken ct = default)
    {
        await EnsureValidTokenAsync();

        var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/crm/{ApiVersion}/objects/{fromObjectType}/{fromObjectId}/associations/{toObjectType}/{toObjectId}/{associationTypeId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRateLimitAsync(() => _httpClient!.SendAsync(request, ct));
        response.EnsureSuccessStatusCode();
    }

    #endregion

    #region Data Connector Implementation

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to HubSpot");

        await EnsureValidTokenAsync();

        var objectType = query.TableOrCollection ?? "contacts";
        var limit = Math.Min(query.Limit ?? 100, 100);
        string? after = null;
        long position = 0;

        // If filter is provided, use search API; otherwise, use list API
        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            // Parse filter as JSON array of SearchFilter objects
            var filters = JsonSerializer.Deserialize<List<SearchFilter>>(query.Filter);

            do
            {
                var result = await SearchObjectsAsync(
                    objectType,
                    filters,
                    properties: query.Fields,
                    limit: limit,
                    after: after,
                    ct: ct);

                foreach (var item in result.Results)
                {
                    if (ct.IsCancellationRequested) yield break;
                    yield return new DataRecord(item, position++, DateTimeOffset.UtcNow);
                }

                after = result.PagingCursor;
            } while (after != null && (query.Limit == null || position < query.Limit));
        }
        else
        {
            // Use standard list API for non-filtered queries
            do
            {
                var url = $"/crm/{ApiVersion}/objects/{objectType}?limit={limit}";
                if (query.Fields != null && query.Fields.Length > 0)
                {
                    url += $"&properties={string.Join(",", query.Fields)}";
                }
                if (after != null)
                {
                    url += $"&after={after}";
                }

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

                var response = await ExecuteWithRateLimitAsync(() => _httpClient.SendAsync(request, ct));
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                foreach (var result in root.GetProperty("results").EnumerateArray())
                {
                    if (ct.IsCancellationRequested) yield break;

                    var values = ParseProperties(result.GetProperty("properties"));
                    values["id"] = result.GetProperty("id").GetString();

                    yield return new DataRecord(values, position++, DateTimeOffset.UtcNow);
                }

                after = root.TryGetProperty("paging", out var paging) &&
                        paging.TryGetProperty("next", out var next) &&
                        next.TryGetProperty("after", out var afterProp)
                    ? afterProp.GetString()
                    : null;

            } while (after != null && (query.Limit == null || position < query.Limit));
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to HubSpot");

        await EnsureValidTokenAsync();

        var objectType = options.TargetTable ?? throw new ArgumentException("TargetTable (object type) is required");
        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        var batchSize = Math.Min(options.BatchSize, 100);
        var batch = new List<DataRecord>();

        await foreach (var record in records.WithCancellation(ct))
        {
            batch.Add(record);

            if (batch.Count >= batchSize)
            {
                var (w, f, e) = await WriteBatchAsync(objectType, batch, options.Mode, ct);
                written += w;
                failed += f;
                if (e != null) errors.AddRange(e);
                batch.Clear();
            }
        }

        // Write remaining records
        if (batch.Count > 0)
        {
            var (w, f, e) = await WriteBatchAsync(objectType, batch, options.Mode, ct);
            written += w;
            failed += f;
            if (e != null) errors.AddRange(e);
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <summary>
    /// Writes a batch of records to HubSpot.
    /// </summary>
    private async Task<(long Written, long Failed, List<string>? Errors)> WriteBatchAsync(
        string objectType,
        List<DataRecord> records,
        SDK.Connectors.WriteMode mode,
        CancellationToken ct)
    {
        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        try
        {
            switch (mode)
            {
                case SDK.Connectors.WriteMode.Insert:
                    var createObjects = records.Select(r => r.Values).ToList();
                    var createResults = await BatchCreateAsync(objectType, createObjects, ct);
                    written = createResults.Count(r => r.Success);
                    failed = createResults.Count(r => !r.Success);
                    errors.AddRange(createResults.Where(r => !r.Success).Select(r => r.ErrorMessage ?? "Unknown error"));
                    break;

                case SDK.Connectors.WriteMode.Update or SDK.Connectors.WriteMode.Upsert:
                    var updateList = records
                        .Where(r => r.Values.ContainsKey("id") || r.Values.ContainsKey("Id"))
                        .Select(r =>
                        {
                            var id = r.Values.GetValueOrDefault("id")?.ToString() ??
                                     r.Values.GetValueOrDefault("Id")?.ToString() ??
                                     throw new InvalidOperationException("Record must have 'id' field for update");
                            var props = r.Values.Where(kv => kv.Key != "id" && kv.Key != "Id")
                                .ToDictionary(kv => kv.Key, kv => kv.Value);
                            return (id, props);
                        }).ToList();

                    var updateResults = await BatchUpdateAsync(objectType, updateList, ct);
                    written = updateResults.Count(r => r.Success);
                    failed = updateResults.Count(r => !r.Success);
                    errors.AddRange(updateResults.Where(r => !r.Success).Select(r => r.ErrorMessage ?? "Unknown error"));
                    break;

                case SDK.Connectors.WriteMode.Delete:
                    foreach (var record in records)
                    {
                        try
                        {
                            var id = record.Values.GetValueOrDefault("id")?.ToString() ??
                                     record.Values.GetValueOrDefault("Id")?.ToString() ??
                                     throw new InvalidOperationException("Record must have 'id' field for delete");
                            await DeleteObjectAsync(objectType, id, ct);
                            written++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            errors.Add($"Delete failed for record at position {record.Position}: {ex.Message}");
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            failed = records.Count;
            errors.Add($"Batch operation failed: {ex.Message}");
        }

        return (written, failed, errors.Count > 0 ? errors : null);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Parses HubSpot properties from JSON element to dictionary.
    /// </summary>
    private static Dictionary<string, object?> ParseProperties(JsonElement properties)
    {
        var result = new Dictionary<string, object?>();
        foreach (var prop in properties.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.ToString()
            };
        }
        return result;
    }

    /// <summary>
    /// Parses batch operation results from JSON.
    /// </summary>
    private static List<BatchOperationResult> ParseBatchResults(JsonElement root)
    {
        var results = new List<BatchOperationResult>();

        if (root.TryGetProperty("results", out var resultsArray))
        {
            foreach (var result in resultsArray.EnumerateArray())
            {
                var id = result.GetProperty("id").GetString()!;
                var props = ParseProperties(result.GetProperty("properties"));
                results.Add(new BatchOperationResult(true, id, props, null));
            }
        }

        if (root.TryGetProperty("errors", out var errorsArray))
        {
            foreach (var error in errorsArray.EnumerateArray())
            {
                var message = error.GetProperty("message").GetString()!;
                results.Add(new BatchOperationResult(false, null, null, message));
            }
        }

        return results;
    }

    #endregion

    #region Plugin Lifecycle

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();

    #endregion
}

#region Supporting Types

/// <summary>
/// Represents a search filter for HubSpot CRM Search API.
/// </summary>
public class SearchFilter
{
    /// <summary>
    /// Property name to filter on.
    /// </summary>
    [JsonPropertyName("propertyName")]
    public string PropertyName { get; set; } = "";

    /// <summary>
    /// Comparison operator (EQ, NEQ, LT, LTE, GT, GTE, BETWEEN, IN, NOT_IN, HAS_PROPERTY, NOT_HAS_PROPERTY, CONTAINS_TOKEN, NOT_CONTAINS_TOKEN).
    /// </summary>
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "EQ";

    /// <summary>
    /// Value to compare against.
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

/// <summary>
/// Represents a sort specification for search results.
/// </summary>
public class SearchSort
{
    /// <summary>
    /// Property name to sort by.
    /// </summary>
    [JsonPropertyName("propertyName")]
    public string PropertyName { get; set; } = "";

    /// <summary>
    /// Sort direction (ASCENDING or DESCENDING).
    /// </summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "DESCENDING";
}

/// <summary>
/// Result of a search operation.
/// </summary>
/// <param name="Results">List of matching objects.</param>
/// <param name="PagingCursor">Cursor for next page of results.</param>
public record SearchResult(
    List<Dictionary<string, object?>> Results,
    string? PagingCursor
);

/// <summary>
/// Result of a batch operation on a single object.
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Id">Object ID (if successful).</param>
/// <param name="Properties">Object properties (if successful).</param>
/// <param name="ErrorMessage">Error message (if failed).</param>
public record BatchOperationResult(
    bool Success,
    string? Id,
    Dictionary<string, object?>? Properties,
    string? ErrorMessage
);

#endregion
