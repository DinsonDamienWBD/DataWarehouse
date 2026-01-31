using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Connectors;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.DataConnectors;

/// <summary>
/// Production-ready Salesforce SaaS connector plugin.
/// Provides CRM data access via REST API with OAuth 2.0 authentication.
/// Supports Bulk API 2.0 for large data operations, SOQL injection protection, and comprehensive CRUD operations.
/// </summary>
public class SalesforceConnectorPlugin : SaaSConnectorPluginBase
{
    private HttpClient? _httpClient;
    private string? _instanceUrl;
    private string? _refreshToken;
    private string? _clientId;
    private string? _clientSecret;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private const int MaxRetries = 5;
    private const int BatchSize = 200; // Salesforce REST API batch limit
    private const int BulkApiThreshold = 2000; // Use Bulk API for operations larger than this
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public override string Id => "datawarehouse.connector.salesforce";

    /// <inheritdoc />
    public override string Name => "Salesforce Connector";

    /// <inheritdoc />
    public override string Version => "2.0.0";

    /// <inheritdoc />
    public override string ConnectorId => "salesforce";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <inheritdoc />
    protected override async Task<ConnectionResult> EstablishConnectionAsync(ConnectorConfig config, CancellationToken ct)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var props = (IReadOnlyDictionary<string, string?>)config.Properties;
            _clientId = props.GetValueOrDefault("ClientId", "");
            _clientSecret = props.GetValueOrDefault("ClientSecret", "");
            var username = props.GetValueOrDefault("Username", "");
            var password = props.GetValueOrDefault("Password", "");
            var securityToken = props.GetValueOrDefault("SecurityToken", "");

            if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
            {
                return new ConnectionResult(false, "ClientId and ClientSecret are required", null);
            }

            // Authenticate using OAuth 2.0 password flow
            AccessToken = await AuthenticateAsync(config);

            // Get instance URL from token response
            _instanceUrl = props.GetValueOrDefault("InstanceUrl", "https://login.salesforce.com");

            var serverInfo = new Dictionary<string, object>
            {
                ["InstanceUrl"] = _instanceUrl,
                ["AuthType"] = "OAuth2",
                ["ApiVersion"] = "v57.0",
                ["BulkApiSupport"] = true
            };

            return new ConnectionResult(true, null, serverInfo);
        }
        catch (Exception ex)
        {
            return new ConnectionResult(false, $"Salesforce connection failed: {ex.Message}", null);
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
        var username = props.GetValueOrDefault("Username", "");
 var password = props.GetValueOrDefault("Password", "");
 var securityToken = props.GetValueOrDefault("SecurityToken", "");
 var loginUrl = props.GetValueOrDefault("LoginUrl", "https://login.salesforce.com");

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
            ["username"] = username,
            ["password"] = password + securityToken
        });

        var response = await ExecuteWithRetryAsync(
            () => _httpClient!.PostAsync($"{loginUrl}/services/oauth2/token", content),
            "Authentication");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _instanceUrl = root.GetProperty("instance_url").GetString();
        _refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        TokenExpiry = DateTimeOffset.UtcNow.AddHours(1); // Salesforce tokens typically last 2 hours

        return root.GetProperty("access_token").GetString()!;
    }

    /// <inheritdoc />
    protected override async Task RefreshTokenAsync()
    {
        if (string.IsNullOrEmpty(_refreshToken))
        {
            throw new InvalidOperationException("No refresh token available");
        }

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = _clientId!,
            ["client_secret"] = _clientSecret!,
            ["refresh_token"] = _refreshToken
        });

        var response = await ExecuteWithRetryAsync(
            () => _httpClient!.PostAsync($"{_instanceUrl}/services/oauth2/token", content),
            "Token refresh");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        AccessToken = doc.RootElement.GetProperty("access_token").GetString();
        TokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
    }

    /// <inheritdoc />
    protected override async Task CloseConnectionAsync()
    {
        await _connectionLock.WaitAsync();
        try
        {
            _httpClient?.Dispose();
            _httpClient = null;
            AccessToken = null;
            _instanceUrl = null;
            _refreshToken = null;
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
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_instanceUrl}/services/data/v57.0/");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            var response = await _httpClient.SendAsync(request);
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
            throw new InvalidOperationException("Not connected to Salesforce");

        await EnsureValidTokenAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"{_instanceUrl}/services/data/v57.0/sobjects/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRetryAsync(
            () => _httpClient.SendAsync(request),
            "Schema fetch");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var sobjects = doc.RootElement.GetProperty("sobjects");

        var objectNames = new List<string>();
        foreach (var obj in sobjects.EnumerateArray())
        {
            objectNames.Add(obj.GetProperty("name").GetString()!);
        }

        return new DataSchema(
            Name: "salesforce",
            Fields: new[]
            {
                new DataSchemaField("Id", "string", false, null, null),
                new DataSchemaField("Name", "string", true, null, null),
                new DataSchemaField("CreatedDate", "datetime", false, null, null),
                new DataSchemaField("LastModifiedDate", "datetime", false, null, null)
            },
            PrimaryKeys: new[] { "Id" },
            Metadata: new Dictionary<string, object>
            {
                ["ObjectCount"] = objectNames.Count,
                ["Objects"] = objectNames.ToArray()
            }
        );
    }

    /// <summary>
    /// Fetches detailed schema for a specific Salesforce object including all fields.
    /// </summary>
    public async Task<DataSchema> FetchObjectSchemaAsync(string sobjectName, CancellationToken ct = default)
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to Salesforce");

        await EnsureValidTokenAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"{_instanceUrl}/services/data/v57.0/sobjects/{sobjectName}/describe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRetryAsync(
            () => _httpClient.SendAsync(request, ct),
            $"Object schema fetch for {sobjectName}");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var fields = new List<DataSchemaField>();
        var primaryKeys = new List<string>();

        foreach (var field in root.GetProperty("fields").EnumerateArray())
        {
            var fieldName = field.GetProperty("name").GetString()!;
            var fieldType = field.GetProperty("type").GetString()!;
            var isNullable = field.GetProperty("nillable").GetBoolean();

            var mappedType = MapSalesforceType(fieldType);
            fields.Add(new DataSchemaField(fieldName, mappedType, isNullable, null, null));

            if (field.TryGetProperty("idLookup", out var idLookup) && idLookup.GetBoolean())
            {
                primaryKeys.Add(fieldName);
            }
        }

        return new DataSchema(
            Name: sobjectName,
            Fields: fields.ToArray(),
            PrimaryKeys: primaryKeys.Count > 0 ? primaryKeys.ToArray() : new[] { "Id" },
            Metadata: new Dictionary<string, object>
            {
                ["Label"] = root.GetProperty("label").GetString() ?? sobjectName,
                ["Createable"] = root.GetProperty("createable").GetBoolean(),
                ["Updateable"] = root.GetProperty("updateable").GetBoolean(),
                ["Deletable"] = root.GetProperty("deletable").GetBoolean()
            }
        );
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<DataRecord> ExecuteReadAsync(
        DataQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to Salesforce");

        await EnsureValidTokenAsync();

        var sobject = query.TableOrCollection ?? "Account";

        // Validate object name to prevent injection
        ValidateSObjectName(sobject);

        var fields = query.Fields ?? new[] { "Id", "Name" };
        ValidateFieldNames(fields);

        var soql = $"SELECT {string.Join(",", fields)} FROM {sobject}";

        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            // Validate and sanitize filter to prevent SOQL injection
            var sanitizedFilter = ValidateAndSanitizeFilter(query.Filter);
            soql += $" WHERE {sanitizedFilter}";
        }
        if (query.Limit.HasValue)
        {
            soql += $" LIMIT {query.Limit.Value}";
        }

        var url = $"{_instanceUrl}/services/data/v57.0/query?q={Uri.EscapeDataString(soql)}";
        long position = 0;

        while (!string.IsNullOrEmpty(url))
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

            var response = await ExecuteWithRetryAsync(
                () => _httpClient.SendAsync(request, ct),
                "Query execution");

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var record in root.GetProperty("records").EnumerateArray())
            {
                if (ct.IsCancellationRequested) yield break;

                var values = new Dictionary<string, object?>();
                foreach (var prop in record.EnumerateObject())
                {
                    if (prop.Name != "attributes")
                    {
                        values[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.ToString();
                    }
                }

                yield return new DataRecord(values, position++, DateTimeOffset.UtcNow);
            }

            url = root.TryGetProperty("nextRecordsUrl", out var nextUrl) && nextUrl.ValueKind != JsonValueKind.Null
                ? $"{_instanceUrl}{nextUrl.GetString()}"
                : null!;
        }
    }

    /// <inheritdoc />
    protected override async Task<WriteResult> ExecuteWriteAsync(
        IAsyncEnumerable<DataRecord> records,
        WriteOptions options,
        CancellationToken ct)
    {
        if (_httpClient == null || string.IsNullOrEmpty(AccessToken))
            throw new InvalidOperationException("Not connected to Salesforce");

        await EnsureValidTokenAsync();

        var sobject = options.TargetTable ?? throw new ArgumentException("Target SObject is required");
        ValidateSObjectName(sobject);

        // Determine operation mode
        var operation = options.Mode.ToLowerInvariant() switch
        {
            "insert" or "create" => "insert",
            "update" => "update",
            "upsert" => "upsert",
            "delete" => "delete",
            _ => "insert"
        };

        // Collect records to determine if we should use Bulk API
        var recordList = new List<DataRecord>();
        await foreach (var record in records.WithCancellation(ct))
        {
            recordList.Add(record);
        }

        // Use Bulk API for large operations
        if (recordList.Count >= BulkApiThreshold)
        {
            return await ExecuteBulkWriteAsync(recordList, sobject, operation, ct);
        }

        // Use batch REST API for smaller operations
        return await ExecuteBatchWriteAsync(recordList, sobject, operation, ct);
    }

    /// <summary>
    /// Executes batch write operations using REST API Composite endpoint.
    /// </summary>
    private async Task<WriteResult> ExecuteBatchWriteAsync(
        List<DataRecord> records,
        string sobject,
        string operation,
        CancellationToken ct)
    {
        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        // Process in batches
        for (int i = 0; i < records.Count; i += BatchSize)
        {
            if (ct.IsCancellationRequested) break;

            var batch = records.Skip(i).Take(BatchSize).ToList();

            if (operation == "delete")
            {
                // Delete operations
                foreach (var record in batch)
                {
                    var result = await ExecuteDeleteAsync(sobject, record, ct);
                    if (result.success)
                    {
                        written++;
                    }
                    else
                    {
                        failed++;
                        errors.Add(result.error ?? "Unknown error");
                    }
                }
            }
            else
            {
                // Create/Update operations using Composite API
                var compositeResult = await ExecuteCompositeAsync(sobject, batch, operation, ct);
                written += compositeResult.written;
                failed += compositeResult.failed;
                if (compositeResult.errors != null)
                {
                    errors.AddRange(compositeResult.errors);
                }
            }
        }

        return new WriteResult(written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <summary>
    /// Executes write operations using Salesforce Bulk API 2.0 for large data volumes.
    /// </summary>
    private async Task<WriteResult> ExecuteBulkWriteAsync(
        List<DataRecord> records,
        string sobject,
        string operation,
        CancellationToken ct)
    {
        await EnsureValidTokenAsync();

        // Step 1: Create bulk job
        var jobId = await CreateBulkJobAsync(sobject, operation, ct);

        try
        {
            // Step 2: Upload data
            await UploadBulkDataAsync(jobId, records, ct);

            // Step 3: Close job to start processing
            await CloseBulkJobAsync(jobId, ct);

            // Step 4: Poll for completion
            var jobInfo = await PollBulkJobAsync(jobId, ct);

            return new WriteResult(
                jobInfo.recordsProcessed - jobInfo.recordsFailed,
                jobInfo.recordsFailed,
                jobInfo.recordsFailed > 0 ? new[] { "Some records failed. Check Salesforce bulk job logs." } : null
            );
        }
        catch (Exception ex)
        {
            // Abort the job on error
            try
            {
                await AbortBulkJobAsync(jobId, ct);
            }
            catch
            {
                // Ignore abort errors
            }
            throw new InvalidOperationException($"Bulk operation failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Creates a Bulk API 2.0 job.
    /// </summary>
    private async Task<string> CreateBulkJobAsync(string sobject, string operation, CancellationToken ct)
    {
        var jobRequest = new
        {
            @object = sobject,
            operation = operation,
            contentType = "CSV",
            lineEnding = "LF"
        };

        var content = new StringContent(
            JsonSerializer.Serialize(jobRequest),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_instanceUrl}/services/data/v57.0/jobs/ingest")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRetryAsync(
            () => _httpClient!.SendAsync(request, ct),
            "Bulk job creation");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    /// Uploads data to a Bulk API job in CSV format.
    /// </summary>
    private async Task UploadBulkDataAsync(string jobId, List<DataRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return;

        // Convert records to CSV
        var csv = new StringBuilder();

        // Header
        var fields = records[0].Values.Keys.ToList();
        csv.AppendLine(string.Join(",", fields.Select(EscapeCsvField)));

        // Data rows
        foreach (var record in records)
        {
            var values = fields.Select(f => record.Values.GetValueOrDefault(f)?.ToString() ?? "");
            csv.AppendLine(string.Join(",", values.Select(EscapeCsvField)));
        }

        var content = new StringContent(csv.ToString(), Encoding.UTF8, "text/csv");

        var request = new HttpRequestMessage(HttpMethod.Put, $"{_instanceUrl}/services/data/v57.0/jobs/ingest/{jobId}/batches")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRetryAsync(
            () => _httpClient!.SendAsync(request, ct),
            "Bulk data upload");

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Closes a Bulk API job to start processing.
    /// </summary>
    private async Task CloseBulkJobAsync(string jobId, CancellationToken ct)
    {
        var stateRequest = new { state = "UploadComplete" };
        var content = new StringContent(
            JsonSerializer.Serialize(stateRequest),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(new HttpMethod("PATCH"), $"{_instanceUrl}/services/data/v57.0/jobs/ingest/{jobId}")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRetryAsync(
            () => _httpClient!.SendAsync(request, ct),
            "Bulk job close");

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Polls a Bulk API job until completion.
    /// </summary>
    private async Task<(long recordsProcessed, long recordsFailed)> PollBulkJobAsync(string jobId, CancellationToken ct)
    {
        var maxPollAttempts = 60; // 5 minutes with 5-second intervals
        var pollInterval = TimeSpan.FromSeconds(5);

        for (int i = 0; i < maxPollAttempts; i++)
        {
            if (ct.IsCancellationRequested) break;

            var request = new HttpRequestMessage(HttpMethod.Get, $"{_instanceUrl}/services/data/v57.0/jobs/ingest/{jobId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

            var response = await _httpClient!.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var state = root.GetProperty("state").GetString();

            if (state == "JobComplete" || state == "Failed" || state == "Aborted")
            {
                var recordsProcessed = root.GetProperty("numberRecordsProcessed").GetInt64();
                var recordsFailed = root.GetProperty("numberRecordsFailed").GetInt64();
                return (recordsProcessed, recordsFailed);
            }

            await Task.Delay(pollInterval, ct);
        }

        throw new TimeoutException("Bulk job did not complete within timeout period");
    }

    /// <summary>
    /// Aborts a Bulk API job.
    /// </summary>
    private async Task AbortBulkJobAsync(string jobId, CancellationToken ct)
    {
        var stateRequest = new { state = "Aborted" };
        var content = new StringContent(
            JsonSerializer.Serialize(stateRequest),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(new HttpMethod("PATCH"), $"{_instanceUrl}/services/data/v57.0/jobs/ingest/{jobId}")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        await _httpClient!.SendAsync(request, ct);
    }

    /// <summary>
    /// Executes composite request for batch create/update operations.
    /// </summary>
    private async Task<(long written, long failed, string[]? errors)> ExecuteCompositeAsync(
        string sobject,
        List<DataRecord> records,
        string operation,
        CancellationToken ct)
    {
        var subrequests = new List<object>();

        for (int i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var method = operation == "update" ? "PATCH" : "POST";
            var url = operation == "update" && record.Values.ContainsKey("Id")
                ? $"/services/data/v57.0/sobjects/{sobject}/{record.Values["Id"]}"
                : $"/services/data/v57.0/sobjects/{sobject}";

            subrequests.Add(new
            {
                method = method,
                url = url,
                referenceId = $"ref{i}",
                body = record.Values
            });
        }

        var compositeRequest = new
        {
            allOrNone = false,
            compositeRequest = subrequests
        };

        var content = new StringContent(
            JsonSerializer.Serialize(compositeRequest),
            Encoding.UTF8,
            "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_instanceUrl}/services/data/v57.0/composite")
        {
            Content = content
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        var response = await ExecuteWithRetryAsync(
            () => _httpClient!.SendAsync(request, ct),
            "Composite request");

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var results = doc.RootElement.GetProperty("compositeResponse");

        long written = 0;
        long failed = 0;
        var errors = new List<string>();

        foreach (var result in results.EnumerateArray())
        {
            var httpStatusCode = result.GetProperty("httpStatusCode").GetInt32();
            if (httpStatusCode >= 200 && httpStatusCode < 300)
            {
                written++;
            }
            else
            {
                failed++;
                var refId = result.GetProperty("referenceId").GetString();
                var body = result.GetProperty("body").ToString();
                errors.Add($"Record {refId}: {body}");
            }
        }

        return (written, failed, errors.Count > 0 ? errors.ToArray() : null);
    }

    /// <summary>
    /// Executes a single delete operation.
    /// </summary>
    private async Task<(bool success, string? error)> ExecuteDeleteAsync(
        string sobject,
        DataRecord record,
        CancellationToken ct)
    {
        if (!record.Values.ContainsKey("Id"))
        {
            return (false, "Record must have an Id field for delete operation");
        }

        var id = record.Values["Id"]?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            return (false, "Record Id cannot be null or empty");
        }

        var request = new HttpRequestMessage(HttpMethod.Delete, $"{_instanceUrl}/services/data/v57.0/sobjects/{sobject}/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        try
        {
            var response = await ExecuteWithRetryAsync(
                () => _httpClient!.SendAsync(request, ct),
                "Delete operation");

            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }
            else
            {
                var errorJson = await response.Content.ReadAsStringAsync(ct);
                return (false, errorJson);
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Validates SObject name to prevent SOQL injection.
    /// </summary>
    private static void ValidateSObjectName(string name)
    {
        if (!Regex.IsMatch(name, @"^[a-zA-Z_][a-zA-Z0-9_]*(__c)?$"))
        {
            throw new ArgumentException($"Invalid SObject name: {name}");
        }
    }

    /// <summary>
    /// Validates field names to prevent SOQL injection.
    /// </summary>
    private static void ValidateFieldNames(string[] fields)
    {
        foreach (var field in fields)
        {
            if (!Regex.IsMatch(field, @"^[a-zA-Z_][a-zA-Z0-9_\.]*(__c|__r)?$"))
            {
                throw new ArgumentException($"Invalid field name: {field}");
            }
        }
    }

    /// <summary>
    /// Validates and sanitizes SOQL filter to prevent injection attacks.
    /// </summary>
    private static string ValidateAndSanitizeFilter(string filter)
    {
        // Basic validation - reject potentially dangerous patterns
        var dangerousPatterns = new[]
        {
            @";\s*DELETE\s+",
            @";\s*INSERT\s+",
            @";\s*UPDATE\s+",
            @"--",
            @"/\*",
            @"\*/",
            @"\bEXEC\b",
            @"\bEXECUTE\b"
        };

        foreach (var pattern in dangerousPatterns)
        {
            if (Regex.IsMatch(filter, pattern, RegexOptions.IgnoreCase))
            {
                throw new ArgumentException($"Filter contains potentially dangerous pattern: {pattern}");
            }
        }

        // Escape single quotes
        return filter.Replace("'", "\\'");
    }

    /// <summary>
    /// Executes an HTTP request with exponential backoff retry logic for rate limits.
    /// </summary>
    private static async Task<HttpResponseMessage> ExecuteWithRetryAsync(
        Func<Task<HttpResponseMessage>> operation,
        string operationName)
    {
        var delay = InitialRetryDelay;

        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var response = await operation();

                // Check for rate limiting
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    if (attempt == MaxRetries)
                    {
                        throw new InvalidOperationException($"{operationName} failed: Rate limit exceeded after {MaxRetries} retries");
                    }

                    // Check for Retry-After header
                    if (response.Headers.TryGetValues("Retry-After", out var retryAfterValues))
                    {
                        if (int.TryParse(retryAfterValues.First(), out var retryAfterSeconds))
                        {
                            delay = TimeSpan.FromSeconds(retryAfterSeconds);
                        }
                    }

                    await Task.Delay(delay);
                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2); // Exponential backoff
                    continue;
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(delay);
                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2);
            }
        }

        throw new InvalidOperationException($"{operationName} failed after {MaxRetries} retries");
    }

    /// <summary>
    /// Maps Salesforce field types to generic data types.
    /// </summary>
    private static string MapSalesforceType(string sfType)
    {
        return sfType.ToLowerInvariant() switch
        {
            "id" => "string",
            "string" => "string",
            "textarea" => "string",
            "email" => "string",
            "url" => "string",
            "phone" => "string",
            "picklist" => "string",
            "multipicklist" => "string",
            "reference" => "string",
            "int" => "integer",
            "double" => "double",
            "currency" => "decimal",
            "percent" => "decimal",
            "date" => "date",
            "datetime" => "datetime",
            "time" => "time",
            "boolean" => "boolean",
            _ => "string"
        };
    }

    /// <summary>
    /// Escapes a CSV field value.
    /// </summary>
    private static string EscapeCsvField(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc />
    public override Task StopAsync() => DisconnectAsync();
}
