using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts.Interface;

#region API Model Types

/// <summary>
/// Describes the location of an API parameter in the request.
/// </summary>
public enum ApiParameterLocation
{
    /// <summary>Parameter appears in the URL path.</summary>
    Path,
    /// <summary>Parameter appears in the query string.</summary>
    Query,
    /// <summary>Parameter appears in the request headers.</summary>
    Header
}

/// <summary>
/// A property within an API data type schema.
/// </summary>
public sealed record ApiProperty
{
    /// <summary>Property name.</summary>
    public required string Name { get; init; }

    /// <summary>Type name (string, int, long, bool, datetime, bytes, object, array).</summary>
    public required string Type { get; init; }

    /// <summary>Whether the property can be null.</summary>
    public bool Nullable { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>If Type is "array", the element type name.</summary>
    public string? ArrayElementType { get; init; }

    /// <summary>If Type is "object", the referenced type name.</summary>
    public string? ReferenceTypeName { get; init; }
}

/// <summary>
/// A data type schema used in API request/response bodies.
/// </summary>
public sealed record ApiDataType
{
    /// <summary>Unique type name (PascalCase, e.g., "StoreRequest").</summary>
    public required string Name { get; init; }

    /// <summary>Properties of this data type.</summary>
    public IReadOnlyDictionary<string, ApiProperty> Properties { get; init; } =
        new Dictionary<string, ApiProperty>();

    /// <summary>Whether this type represents an array of items.</summary>
    public bool IsArray { get; init; }

    /// <summary>If IsArray, the element type name.</summary>
    public string? ArrayElementTypeName { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// A parameter for an API endpoint.
/// </summary>
public sealed record ApiParameter
{
    /// <summary>Parameter name.</summary>
    public required string Name { get; init; }

    /// <summary>Type (string, int, bool, datetime).</summary>
    public required string Type { get; init; }

    /// <summary>Whether the parameter is required.</summary>
    public bool Required { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Where the parameter appears (path, query, header).</summary>
    public ApiParameterLocation In { get; init; } = ApiParameterLocation.Query;
}

/// <summary>
/// An API endpoint generated from a plugin capability.
/// </summary>
public sealed record ApiEndpoint
{
    /// <summary>URL path template (e.g., "/api/v1/storage/{key}").</summary>
    public required string Path { get; init; }

    /// <summary>HTTP method (GET, POST, PUT, DELETE).</summary>
    public required HttpMethod Method { get; init; }

    /// <summary>Source plugin identifier.</summary>
    public required string PluginId { get; init; }

    /// <summary>Capability name this endpoint exposes.</summary>
    public required string CapabilityName { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Endpoint parameters.</summary>
    public IReadOnlyList<ApiParameter> Parameters { get; init; } = Array.Empty<ApiParameter>();

    /// <summary>Request body schema, if applicable.</summary>
    public ApiDataType? RequestBody { get; init; }

    /// <summary>Response body schema.</summary>
    public ApiDataType? ResponseBody { get; init; }

    /// <summary>Whether this endpoint uses streaming (WebSocket/SSE).</summary>
    public bool IsStreaming { get; init; }

    /// <summary>OpenAPI operation ID (unique identifier for code generation).</summary>
    public string OperationId => $"{PluginId}_{CapabilityName}_{Method}".Replace(".", "_").Replace("-", "_");

    /// <summary>Tag for grouping (typically plugin ID).</summary>
    public string Tag => PluginId;
}

/// <summary>
/// The complete dynamic API model generated from plugin capabilities.
/// Contains all endpoints and data types needed to produce protocol-specific definitions.
/// </summary>
public sealed record DynamicApiModel
{
    /// <summary>All API endpoints.</summary>
    public IReadOnlyList<ApiEndpoint> Endpoints { get; init; } = Array.Empty<ApiEndpoint>();

    /// <summary>All data type schemas referenced by endpoints.</summary>
    public IReadOnlyList<ApiDataType> DataTypes { get; init; } = Array.Empty<ApiDataType>();

    /// <summary>Timestamp when this model was generated.</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Number of plugins contributing to this model.</summary>
    public int PluginCount { get; init; }

    /// <summary>API version string.</summary>
    public string ApiVersion { get; init; } = "1.0.0";
}

#endregion

/// <summary>
/// Generates a <see cref="DynamicApiModel"/> from the <see cref="IPluginCapabilityRegistry"/>.
/// Converts registered plugin capabilities into REST, gRPC, GraphQL, and WebSocket endpoints
/// using convention-based mapping. Subscribes to capability change events to keep the model current.
/// </summary>
/// <remarks>
/// <para>Convention-based endpoint generation rules:</para>
/// <list type="bullet">
///   <item><description>Storage capabilities: GET/POST/PUT/DELETE /api/v1/storage/{key}</description></item>
///   <item><description>Query/Search/Analytics capabilities: POST /api/v1/query</description></item>
///   <item><description>Management/Governance/Observability: GET/PUT /api/v1/manage/{plugin}/{capability}</description></item>
///   <item><description>Streaming/Pipeline/Replication: WebSocket /ws/v1/{plugin}/{capability}</description></item>
/// </list>
/// </remarks>
public sealed class DynamicApiGenerator : IDisposable
{
    private readonly IPluginCapabilityRegistry _registry;
    private volatile DynamicApiModel _currentModel;
    private readonly BoundedDictionary<string, ApiDataType> _sharedTypes = new BoundedDictionary<string, ApiDataType>(1000);
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _regenerateLock = new();
    private bool _disposed;

    /// <summary>
    /// Raised when the API model is regenerated due to capability changes.
    /// </summary>
    public event EventHandler<DynamicApiModel>? ModelRegenerated;

    /// <summary>
    /// Initializes a new instance of <see cref="DynamicApiGenerator"/>.
    /// </summary>
    /// <param name="registry">The plugin capability registry to read capabilities from.</param>
    public DynamicApiGenerator(IPluginCapabilityRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _currentModel = new DynamicApiModel();
        InitializeSharedTypes();
    }

    /// <summary>
    /// Gets the current API model. Thread-safe via volatile read.
    /// </summary>
    public DynamicApiModel CurrentModel => _currentModel;

    /// <summary>
    /// Starts the generator: builds the initial model and subscribes to capability change events.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Subscribe to change events for auto-update
        _subscriptions.Add(_registry.OnCapabilityRegistered(_ => Regenerate()));
        _subscriptions.Add(_registry.OnCapabilityUnregistered(_ => Regenerate()));
        _subscriptions.Add(_registry.OnAvailabilityChanged((_, _) => Regenerate()));

        // Generate initial model
        Regenerate();
    }

    /// <summary>
    /// Manually regenerates the API model from the current registry state.
    /// </summary>
    /// <returns>The newly generated API model.</returns>
    public DynamicApiModel Regenerate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_regenerateLock)
        {
            var capabilities = _registry.GetAll();
            var endpoints = new List<ApiEndpoint>();
            var dataTypes = new List<ApiDataType>(_sharedTypes.Values);
            var pluginIds = new HashSet<string>();

            foreach (var capability in capabilities)
            {
                if (!capability.IsAvailable) continue;

                pluginIds.Add(capability.PluginId);
                var generated = GenerateEndpointsForCapability(capability);
                endpoints.AddRange(generated);
            }

            var model = new DynamicApiModel
            {
                Endpoints = endpoints.AsReadOnly(),
                DataTypes = dataTypes.AsReadOnly(),
                GeneratedAt = DateTimeOffset.UtcNow,
                PluginCount = pluginIds.Count,
                ApiVersion = "1.0.0"
            };

            _currentModel = model;
            ModelRegenerated?.Invoke(this, model);
            return model;
        }
    }

    /// <summary>
    /// Generates API endpoints for a single registered capability using convention-based mapping.
    /// </summary>
    private IReadOnlyList<ApiEndpoint> GenerateEndpointsForCapability(RegisteredCapability capability)
    {
        var endpoints = new List<ApiEndpoint>();
        var pluginSlug = SanitizeSlug(capability.PluginId);
        var capSlug = SanitizeSlug(capability.CapabilityId);

        switch (capability.Category)
        {
            case CapabilityCategory.Storage:
            case CapabilityCategory.Archival:
                endpoints.AddRange(GenerateStorageEndpoints(capability, pluginSlug, capSlug));
                break;

            case CapabilityCategory.Search:
            case CapabilityCategory.Analytics:
            case CapabilityCategory.Database:
                endpoints.AddRange(GenerateQueryEndpoints(capability, pluginSlug, capSlug));
                break;

            case CapabilityCategory.Pipeline:
            case CapabilityCategory.Replication:
            case CapabilityCategory.Transit:
            case CapabilityCategory.Transport:
                endpoints.AddRange(GenerateStreamingEndpoints(capability, pluginSlug, capSlug));
                break;

            default:
                endpoints.AddRange(GenerateManagementEndpoints(capability, pluginSlug, capSlug));
                break;
        }

        return endpoints;
    }

    private IReadOnlyList<ApiEndpoint> GenerateStorageEndpoints(
        RegisteredCapability cap, string pluginSlug, string capSlug)
    {
        var basePath = $"/api/v1/storage/{pluginSlug}";
        var keyParam = new ApiParameter
        {
            Name = "key",
            Type = "string",
            Required = true,
            Description = "Storage key identifier",
            In = ApiParameterLocation.Path
        };

        return new[]
        {
            new ApiEndpoint
            {
                Path = $"{basePath}/{{key}}",
                Method = HttpMethod.GET,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.get",
                Description = $"Retrieve data by key from {cap.DisplayName}",
                Parameters = new[] { keyParam },
                ResponseBody = CreateDataTypeForCapability(cap, "Response")
            },
            new ApiEndpoint
            {
                Path = basePath,
                Method = HttpMethod.POST,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.store",
                Description = $"Store data via {cap.DisplayName}",
                RequestBody = CreateDataTypeForCapability(cap, "Request"),
                ResponseBody = CreateStoreResponseType()
            },
            new ApiEndpoint
            {
                Path = $"{basePath}/{{key}}",
                Method = HttpMethod.PUT,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.update",
                Description = $"Update data by key in {cap.DisplayName}",
                Parameters = new[] { keyParam },
                RequestBody = CreateDataTypeForCapability(cap, "Request"),
                ResponseBody = CreateStoreResponseType()
            },
            new ApiEndpoint
            {
                Path = $"{basePath}/{{key}}",
                Method = HttpMethod.DELETE,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.delete",
                Description = $"Delete data by key from {cap.DisplayName}",
                Parameters = new[] { keyParam }
            }
        };
    }

    private IReadOnlyList<ApiEndpoint> GenerateQueryEndpoints(
        RegisteredCapability cap, string pluginSlug, string capSlug)
    {
        return new[]
        {
            new ApiEndpoint
            {
                Path = $"/api/v1/query/{pluginSlug}",
                Method = HttpMethod.POST,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.execute",
                Description = $"Execute query via {cap.DisplayName}",
                RequestBody = CreateQueryRequestType(cap),
                ResponseBody = CreateQueryResponseType(cap)
            },
            new ApiEndpoint
            {
                Path = $"/api/v1/query/{pluginSlug}/schema",
                Method = HttpMethod.GET,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.schema",
                Description = $"Get query schema for {cap.DisplayName}"
            }
        };
    }

    private IReadOnlyList<ApiEndpoint> GenerateStreamingEndpoints(
        RegisteredCapability cap, string pluginSlug, string capSlug)
    {
        return new[]
        {
            new ApiEndpoint
            {
                Path = $"/ws/v1/{pluginSlug}/{capSlug}",
                Method = HttpMethod.GET,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.stream",
                Description = $"WebSocket streaming channel for {cap.DisplayName}",
                IsStreaming = true
            },
            new ApiEndpoint
            {
                Path = $"/api/v1/manage/{pluginSlug}/{capSlug}/status",
                Method = HttpMethod.GET,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.status",
                Description = $"Get status of {cap.DisplayName}"
            }
        };
    }

    private IReadOnlyList<ApiEndpoint> GenerateManagementEndpoints(
        RegisteredCapability cap, string pluginSlug, string capSlug)
    {
        return new[]
        {
            new ApiEndpoint
            {
                Path = $"/api/v1/manage/{pluginSlug}/{capSlug}",
                Method = HttpMethod.GET,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.get",
                Description = $"Get configuration for {cap.DisplayName}",
                ResponseBody = CreateManagementResponseType(cap)
            },
            new ApiEndpoint
            {
                Path = $"/api/v1/manage/{pluginSlug}/{capSlug}",
                Method = HttpMethod.PUT,
                PluginId = cap.PluginId,
                CapabilityName = $"{capSlug}.update",
                Description = $"Update configuration for {cap.DisplayName}",
                RequestBody = CreateManagementRequestType(cap)
            }
        };
    }

    #region Data Type Helpers

    private void InitializeSharedTypes()
    {
        _sharedTypes.TryAdd("ErrorResponse", new ApiDataType
        {
            Name = "ErrorResponse",
            Description = "Standard error response",
            Properties = new Dictionary<string, ApiProperty>
            {
                ["error"] = new() { Name = "error", Type = "string", Description = "Error message" },
                ["code"] = new() { Name = "code", Type = "string", Description = "Error code", Nullable = true },
                ["details"] = new() { Name = "details", Type = "object", Description = "Additional error details", Nullable = true }
            }
        });

        _sharedTypes.TryAdd("PaginationInfo", new ApiDataType
        {
            Name = "PaginationInfo",
            Description = "Pagination metadata",
            Properties = new Dictionary<string, ApiProperty>
            {
                ["offset"] = new() { Name = "offset", Type = "int", Description = "Current offset" },
                ["limit"] = new() { Name = "limit", Type = "int", Description = "Page size" },
                ["totalCount"] = new() { Name = "totalCount", Type = "long", Description = "Total matching items" },
                ["hasMore"] = new() { Name = "hasMore", Type = "bool", Description = "Whether more items exist" }
            }
        });
    }

    private static ApiDataType CreateDataTypeForCapability(RegisteredCapability cap, string suffix)
    {
        var typeName = $"{SanitizeTypeName(cap.CapabilityId)}{suffix}";
        return new ApiDataType
        {
            Name = typeName,
            Description = $"{suffix} type for {cap.DisplayName}",
            Properties = new Dictionary<string, ApiProperty>
            {
                ["data"] = new() { Name = "data", Type = "bytes", Description = "Binary data payload" },
                ["contentType"] = new() { Name = "contentType", Type = "string", Description = "MIME type of the data", Nullable = true },
                ["metadata"] = new() { Name = "metadata", Type = "object", Description = "Key-value metadata", Nullable = true }
            }
        };
    }

    private static ApiDataType CreateStoreResponseType()
    {
        return new ApiDataType
        {
            Name = "StoreResponse",
            Description = "Response from a store operation",
            Properties = new Dictionary<string, ApiProperty>
            {
                ["key"] = new() { Name = "key", Type = "string", Description = "The key under which data was stored" },
                ["version"] = new() { Name = "version", Type = "long", Description = "Version number after write" },
                ["timestamp"] = new() { Name = "timestamp", Type = "datetime", Description = "When the store completed" }
            }
        };
    }

    private static ApiDataType CreateQueryRequestType(RegisteredCapability cap)
    {
        return new ApiDataType
        {
            Name = $"{SanitizeTypeName(cap.CapabilityId)}QueryRequest",
            Description = $"Query request for {cap.DisplayName}",
            Properties = new Dictionary<string, ApiProperty>
            {
                ["query"] = new() { Name = "query", Type = "string", Description = "Query expression" },
                ["parameters"] = new() { Name = "parameters", Type = "object", Description = "Query parameters", Nullable = true },
                ["offset"] = new() { Name = "offset", Type = "int", Description = "Pagination offset" },
                ["limit"] = new() { Name = "limit", Type = "int", Description = "Maximum results to return" }
            }
        };
    }

    private static ApiDataType CreateQueryResponseType(RegisteredCapability cap)
    {
        return new ApiDataType
        {
            Name = $"{SanitizeTypeName(cap.CapabilityId)}QueryResponse",
            Description = $"Query response from {cap.DisplayName}",
            Properties = new Dictionary<string, ApiProperty>
            {
                ["results"] = new() { Name = "results", Type = "array", Description = "Query result rows", ArrayElementType = "object" },
                ["totalCount"] = new() { Name = "totalCount", Type = "long", Description = "Total matching rows" },
                ["executionTimeMs"] = new() { Name = "executionTimeMs", Type = "long", Description = "Query execution time in milliseconds" }
            }
        };
    }

    private static ApiDataType CreateManagementResponseType(RegisteredCapability cap)
    {
        return new ApiDataType
        {
            Name = $"{SanitizeTypeName(cap.CapabilityId)}Config",
            Description = $"Configuration for {cap.DisplayName}",
            Properties = new Dictionary<string, ApiProperty>
            {
                ["enabled"] = new() { Name = "enabled", Type = "bool", Description = "Whether the capability is enabled" },
                ["settings"] = new() { Name = "settings", Type = "object", Description = "Capability-specific settings" },
                ["status"] = new() { Name = "status", Type = "string", Description = "Current operational status" }
            }
        };
    }

    private static ApiDataType CreateManagementRequestType(RegisteredCapability cap)
    {
        return new ApiDataType
        {
            Name = $"{SanitizeTypeName(cap.CapabilityId)}ConfigUpdate",
            Description = $"Configuration update for {cap.DisplayName}",
            Properties = new Dictionary<string, ApiProperty>
            {
                ["enabled"] = new() { Name = "enabled", Type = "bool", Description = "Whether to enable the capability", Nullable = true },
                ["settings"] = new() { Name = "settings", Type = "object", Description = "Updated settings", Nullable = true }
            }
        };
    }

    private static string SanitizeSlug(string input)
    {
        return input.Replace(".", "-").Replace(" ", "-").ToLowerInvariant();
    }

    private static string SanitizeTypeName(string input)
    {
        // Convert "encryption.aes-256-gcm" to "EncryptionAes256Gcm"
        var parts = input.Split('.', '-', '_', ' ');
        return string.Concat(parts.Select(p =>
            p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : "")));
    }

    #endregion

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
    }
}
