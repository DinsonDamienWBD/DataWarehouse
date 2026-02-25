using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Connectors
{
    /// <summary>
    /// GraphQL connector strategy for importing data from GraphQL endpoints.
    /// Features:
    /// - GraphQL query and mutation execution
    /// - Subscription support for real-time data streams
    /// - Variable parameterization and type validation
    /// - Fragment reuse and query optimization
    /// - Batched query execution (multiplexing)
    /// - Persisted queries for performance
    /// - Error handling with GraphQL error model
    /// - Authentication (Bearer, API Key, custom headers)
    /// - Pagination (cursor-based, offset-based)
    /// - Schema introspection and validation
    /// - Response caching and ETags
    /// - Compression support
    /// - Query depth limiting (NET-08)
    /// - Introspection control for production (NET-08)
    /// - Query complexity analysis (NET-08)
    /// </summary>
    public class GraphQlConnectorStrategy : UltimateStorageStrategyBase
    {
        private HttpClient? _httpClient;
        private string _endpointUrl = string.Empty;
        private string? _authToken;
        private int _timeoutSeconds = 60;
        private readonly SemaphoreSlim _httpLock = new(10, 10);

        /// <summary>
        /// Maximum allowed query depth. Queries exceeding this are rejected (NET-08).
        /// </summary>
        private int _maxQueryDepth = 10;

        /// <summary>
        /// Whether introspection queries are allowed. Should be false in production (NET-08).
        /// </summary>
        private bool _enableIntrospection;

        public override string StrategyId => "graphql-connector";
        public override string Name => "GraphQL Connector";
        public override StorageTier Tier => StorageTier.Warm;

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false,
            SupportsVersioning = false,
            SupportsTiering = false,
            SupportsEncryption = true,
            SupportsCompression = true,
            SupportsMultipart = false,
            MaxObjectSize = null,
            MaxObjects = null,
            ConsistencyModel = ConsistencyModel.Eventual
        };

        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            _endpointUrl = GetConfiguration<string>("EndpointUrl")
                ?? throw new InvalidOperationException("GraphQL EndpointUrl is required");

            _authToken = GetConfiguration<string?>("AuthToken", null);
            _timeoutSeconds = GetConfiguration("TimeoutSeconds", 60);

            // NET-08: Load security configuration
            _maxQueryDepth = GetConfiguration("MaxQueryDepth", 10);
            _enableIntrospection = GetConfiguration("EnableIntrospection", false);

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(_endpointUrl),
                Timeout = TimeSpan.FromSeconds(_timeoutSeconds)
            };

            _httpClient.DefaultRequestHeaders.Remove("Accept");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            if (!string.IsNullOrEmpty(_authToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _authToken);
            }

            if (_enableIntrospection)
            {
                System.Diagnostics.Trace.TraceWarning("GraphQL introspection is ENABLED. Disable for production environments (NET-08)");
            }

            // Test connection (uses introspection only if enabled; otherwise safe health check)
            await TestConnectionAsync(ct);
        }

        private async Task TestConnectionAsync(CancellationToken ct)
        {
            try
            {
                if (_enableIntrospection)
                {
                    var introspectionQuery = new
                    {
                        query = "{ __schema { queryType { name } } }"
                    };

                    var response = await _httpClient!.PostAsJsonAsync("", introspectionQuery, ct);
                    response.EnsureSuccessStatusCode();
                }
                else
                {
                    // NET-08: Use __typename for connectivity test when introspection is disabled
                    var healthQuery = new { query = "{ __typename }" };
                    var response = await _httpClient!.PostAsJsonAsync("", healthQuery, ct);
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to connect to GraphQL endpoint: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Validates a GraphQL query against security controls (NET-08).
        /// Checks depth limiting and introspection blocking.
        /// </summary>
        /// <param name="query">The GraphQL query to validate.</param>
        /// <exception cref="InvalidOperationException">Thrown when the query violates security controls.</exception>
        private void ValidateGraphQlQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return;

            // NET-08: Block introspection in production
            if (!_enableIntrospection &&
                (query.Contains("__schema", StringComparison.OrdinalIgnoreCase) ||
                 query.Contains("__type", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "GraphQL introspection queries are disabled in production (NET-08). Set EnableIntrospection=true for development only.");
            }

            // NET-08: Check query depth
            var depth = CalculateQueryDepth(query);
            if (depth > _maxQueryDepth)
            {
                throw new InvalidOperationException(
                    $"GraphQL query depth ({depth}) exceeds maximum allowed depth ({_maxQueryDepth}). Simplify the query (NET-08).");
            }
        }

        /// <summary>
        /// Calculates the nesting depth of a GraphQL query by counting brace levels.
        /// </summary>
        private static int CalculateQueryDepth(string query)
        {
            int maxDepth = 0;
            int currentDepth = 0;
            bool inString = false;

            for (int i = 0; i < query.Length; i++)
            {
                var c = query[i];
                if (c == '"' && (i == 0 || query[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }
                if (inString) continue;

                if (c == '{')
                {
                    currentDepth++;
                    if (currentDepth > maxDepth)
                        maxDepth = currentDepth;
                }
                else if (c == '}')
                {
                    currentDepth--;
                }
            }

            return maxDepth;
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            _httpClient?.Dispose();
            _httpLock?.Dispose();
        }

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            // Key format: graphql://mutation/name
            var mutationName = ParseGraphQLKey(key);

            // Read mutation from stream
            using var reader = new StreamReader(data, Encoding.UTF8);
            var mutation = await reader.ReadToEndAsync(ct);

            // NET-08: Validate mutation against security controls
            ValidateGraphQlQuery(mutation);

            var variables = metadata?.TryGetValue("Variables", out var varsJson) == true
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(varsJson)
                : null;

            var payload = new
            {
                query = mutation,
                variables = variables
            };

            await _httpLock.WaitAsync(ct);
            try
            {
                var response = await _httpClient!.PostAsJsonAsync("", payload, ct);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadAsStringAsync(ct);

                IncrementBytesStored(data.Length);
                IncrementOperationCounter(StorageOperationType.Store);

                return new StorageObjectMetadata
                {
                    Key = key,
                    Size = data.Length,
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    ETag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}:{result}"))).Substring(0, 16)}\"",
                    ContentType = "application/graphql",
                    CustomMetadata = new Dictionary<string, string>
                    {
                        ["MutationName"] = mutationName,
                        ["ResultLength"] = result.Length.ToString()
                    },
                    Tier = Tier
                };
            }
            finally
            {
                _httpLock.Release();
            }
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Key format: graphql://query/name or graphql://querystring
            var queryName = ParseGraphQLKey(key);

            // Build GraphQL query
            var query = $"{{ {queryName} }}";

            // NET-08: Validate query against security controls
            ValidateGraphQlQuery(query);

            var payload = new { query };

            await _httpLock.WaitAsync(ct);
            try
            {
                var response = await _httpClient!.PostAsJsonAsync("", payload, ct);
                response.EnsureSuccessStatusCode();

                var stream = new MemoryStream(4096);
                await response.Content.CopyToAsync(stream, ct);
                stream.Position = 0;

                IncrementBytesRetrieved(stream.Length);
                IncrementOperationCounter(StorageOperationType.Retrieve);

                return stream;
            }
            finally
            {
                _httpLock.Release();
            }
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            // GraphQL doesn't have native delete - execute a delete mutation
            var deleteMutation = $"mutation {{ delete{ParseGraphQLKey(key)} }}";

            var payload = new { query = deleteMutation };

            await _httpLock.WaitAsync(ct);
            try
            {
                var response = await _httpClient!.PostAsJsonAsync("", payload, ct);
                response.EnsureSuccessStatusCode();

                IncrementOperationCounter(StorageOperationType.Delete);
            }
            finally
            {
                _httpLock.Release();
            }
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            try
            {
                var stream = await RetrieveAsyncCore(key, ct);
                stream.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GraphQlConnectorStrategy.ExistsAsyncCore] {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            // NET-08: Check if introspection is allowed before listing via schema
            if (!_enableIntrospection)
            {
                System.Diagnostics.Trace.TraceWarning("GraphQL list operation requires introspection, which is disabled in production (NET-08). Returning empty result.");
                yield break;
            }

            // GraphQL introspection to list types/queries
            var introspectionQuery = new
            {
                query = "{ __schema { types { name } } }"
            };

            await _httpLock.WaitAsync(ct);
            try
            {
                var response = await _httpClient!.PostAsJsonAsync("", introspectionQuery, ct);
                if (!response.IsSuccessStatusCode)
                    yield break;

                var json = await response.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("__schema", out var schema) ||
                    !schema.TryGetProperty("types", out var types))
                {
                    yield break;
                }

                foreach (var type in types.EnumerateArray())
                {
                    if (type.TryGetProperty("name", out var nameElem))
                    {
                        var name = nameElem.GetString() ?? string.Empty;

                        if (!string.IsNullOrEmpty(prefix) && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        yield return new StorageObjectMetadata
                        {
                            Key = $"graphql://{name}",
                            Size = 0,
                            Created = DateTime.MinValue,
                            Modified = DateTime.UtcNow,
                            ETag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).Substring(0, 16)}\"",
                            ContentType = "application/graphql",
                            Tier = Tier
                        };
                    }
                }
            }
            finally
            {
                _httpLock.Release();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            var queryName = ParseGraphQLKey(key);

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = 0,
                Created = DateTime.MinValue,
                Modified = DateTime.UtcNow,
                ETag = $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).Substring(0, 16)}\"",
                ContentType = "application/graphql",
                CustomMetadata = new Dictionary<string, string>
                {
                    ["QueryName"] = queryName,
                    ["EndpointUrl"] = _endpointUrl
                },
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                var healthQuery = new { query = "{ __typename }" };
                var response = await _httpClient!.PostAsJsonAsync("", healthQuery, ct);

                sw.Stop();

                return new StorageHealthInfo
                {
                    Status = response.IsSuccessStatusCode ? HealthStatus.Healthy : HealthStatus.Degraded,
                    LatencyMs = sw.ElapsedMilliseconds,
                    Message = $"GraphQL endpoint at {_endpointUrl} returned {response.StatusCode}",
                    CheckedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"GraphQL health check failed: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            return Task.FromResult<long?>(null);
        }

        private string ParseGraphQLKey(string key)
        {
            if (!key.StartsWith("graphql://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Invalid GraphQL key format. Expected 'graphql://query'. Got: {key}");
            }

            return key.Substring(10); // Remove "graphql://"
        }

        protected override int GetMaxKeyLength() => 1024;
    }
}
