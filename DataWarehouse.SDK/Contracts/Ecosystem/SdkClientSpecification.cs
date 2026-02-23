using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

// =============================================================================
// SDK Language Enum
// =============================================================================

/// <summary>
/// Target languages for SDK code generation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public enum SdkLanguage
{
    /// <summary>Python SDK with grpcio, type hints, context managers.</summary>
    Python = 0,
    /// <summary>Java SDK with grpc-java, CompletableFuture, AutoCloseable.</summary>
    Java = 1,
    /// <summary>Go SDK with context.Context, error returns, module path.</summary>
    Go = 2,
    /// <summary>Rust SDK with tonic, Result types, Drop.</summary>
    Rust = 3,
    /// <summary>TypeScript/JavaScript SDK with grpc-js/grpc-web, Promises.</summary>
    TypeScript = 4
}

// =============================================================================
// SDK API Surface — canonical method/type/enum definitions
// =============================================================================

/// <summary>
/// Defines the canonical SDK API surface that every language SDK must implement.
/// Built from proto service definitions in <see cref="DataWarehouseGrpcServices"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed record SdkApiSurface
{
    /// <summary>All SDK methods (connect, store, retrieve, query, etc.).</summary>
    public required IReadOnlyList<SdkMethod> Methods { get; init; }

    /// <summary>All SDK types (request/response models).</summary>
    public required IReadOnlyList<SdkType> Types { get; init; }

    /// <summary>All SDK enums (column types, health status, etc.).</summary>
    public required IReadOnlyList<SdkEnum> Enums { get; init; }
}

/// <summary>
/// A single SDK method mapping to one or more gRPC RPC calls.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed record SdkMethod
{
    /// <summary>Method name in snake_case (e.g., "store", "stream_store").</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>Source gRPC service name.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Source gRPC RPC method name.</summary>
    public required string RpcMethod { get; init; }

    /// <summary>Input type for the method.</summary>
    public required SdkType InputType { get; init; }

    /// <summary>Output type for the method.</summary>
    public required SdkType OutputType { get; init; }

    /// <summary>Whether the server streams the response (e.g., retrieve, query).</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Whether the client streams the request (e.g., stream_store).</summary>
    public bool IsClientStreaming { get; init; }
}

/// <summary>
/// An SDK type definition with named fields.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed record SdkType
{
    /// <summary>Type name in PascalCase.</summary>
    public required string Name { get; init; }

    /// <summary>Fields composing the type.</summary>
    public IReadOnlyList<SdkField> Fields { get; init; } = Array.Empty<SdkField>();
}

/// <summary>
/// A field within an SDK type.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed record SdkField
{
    /// <summary>Field name in snake_case.</summary>
    public required string Name { get; init; }

    /// <summary>Type name (string, bytes, int64, etc.).</summary>
    public required string Type { get; init; }

    /// <summary>Whether the field is nullable/optional.</summary>
    public bool Nullable { get; init; }

    /// <summary>Human-readable description.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// An SDK enum definition with named values.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed record SdkEnum
{
    /// <summary>Enum name in PascalCase.</summary>
    public required string Name { get; init; }

    /// <summary>Enum values with names and integer values.</summary>
    public required IReadOnlyList<(string Name, int Value)> Values { get; init; }
}

// =============================================================================
// SDK Language Binding — per-language packaging and idiom configuration
// =============================================================================

/// <summary>
/// Per-language binding configuration including package metadata and idioms.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed record SdkLanguageBinding
{
    /// <summary>Target language.</summary>
    public required SdkLanguage Language { get; init; }

    /// <summary>Package name (e.g., "datawarehouse" for Python, "com.datawarehouse.sdk" for Java).</summary>
    public required string PackageName { get; init; }

    /// <summary>Package version.</summary>
    public string PackageVersion { get; init; } = "1.0.0";

    /// <summary>Language-specific idioms applied during code generation.</summary>
    public IReadOnlyList<SdkIdiom> Idioms { get; init; } = Array.Empty<SdkIdiom>();
}

/// <summary>
/// A language-specific coding pattern applied during SDK generation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed record SdkIdiom
{
    /// <summary>Pattern name (e.g., "context_manager", "result_type").</summary>
    public required string Pattern { get; init; }

    /// <summary>Human-readable description of the idiom.</summary>
    public required string Description { get; init; }
}

// =============================================================================
// SDK Client Specification — builds the canonical API surface from proto defs
// =============================================================================

/// <summary>
/// Builds the canonical SDK API surface from the proto service definitions,
/// mapping each gRPC RPC method to an idiomatic SDK method. Also provides
/// per-language bindings with appropriate idioms and packaging metadata.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public static class SdkClientSpecification
{
    // -------------------------------------------------------------------------
    // Shared SDK Types (reused across methods)
    // -------------------------------------------------------------------------

    private static readonly SdkType ConnectionOptionsType = new()
    {
        Name = "ConnectionOptions",
        Fields = new SdkField[]
        {
            new() { Name = "host", Type = "string", Description = "Server hostname or IP" },
            new() { Name = "port", Type = "int32", Description = "Server port" },
            new() { Name = "use_tls", Type = "bool", Description = "Enable TLS encryption" },
            new() { Name = "auth_token", Type = "string", Nullable = true, Description = "Authentication token" },
            new() { Name = "timeout_ms", Type = "int32", Description = "Connection timeout in milliseconds" },
            new() { Name = "max_retries", Type = "int32", Description = "Max connection retries" },
            new() { Name = "metadata", Type = "map<string,string>", Description = "Additional connection metadata" }
        }
    };

    private static readonly SdkType ConnectionHandleType = new()
    {
        Name = "ConnectionHandle",
        Fields = new SdkField[]
        {
            new() { Name = "id", Type = "string", Description = "Connection identifier" },
            new() { Name = "connected", Type = "bool", Description = "Whether currently connected" },
            new() { Name = "server_version", Type = "string", Description = "Server version string" }
        }
    };

    private static readonly SdkType StoreRequestType = new()
    {
        Name = "StoreRequest",
        Fields = new SdkField[]
        {
            new() { Name = "key", Type = "string", Description = "Object key" },
            new() { Name = "data", Type = "bytes", Description = "Object data payload" },
            new() { Name = "metadata", Type = "map<string,string>", Description = "User-defined metadata" },
            new() { Name = "tags", Type = "list<string>", Description = "Tags for categorization" },
            new() { Name = "content_type", Type = "string", Nullable = true, Description = "Content type MIME" },
            new() { Name = "expected_version", Type = "int64", Description = "Expected version for CAS, -1 = unconditional" },
            new() { Name = "ttl_seconds", Type = "int64", Description = "TTL in seconds, 0 = no expiry" }
        }
    };

    private static readonly SdkType StoreResponseType = new()
    {
        Name = "StoreResponse",
        Fields = new SdkField[]
        {
            new() { Name = "version", Type = "int64", Description = "Assigned version" },
            new() { Name = "checksum", Type = "string", Description = "SHA-256 checksum hex" },
            new() { Name = "stored_at", Type = "datetime", Description = "Store timestamp" },
            new() { Name = "size_bytes", Type = "int64", Description = "Stored data size" }
        }
    };

    private static readonly SdkType RetrieveRequestType = new()
    {
        Name = "RetrieveRequest",
        Fields = new SdkField[]
        {
            new() { Name = "key", Type = "string", Description = "Object key" },
            new() { Name = "version", Type = "int64", Description = "Version to retrieve, 0 = latest" },
            new() { Name = "include_metadata", Type = "bool", Description = "Include metadata in response" }
        }
    };

    private static readonly SdkType DataStreamType = new()
    {
        Name = "DataStream",
        Fields = new SdkField[]
        {
            new() { Name = "data", Type = "bytes", Description = "Chunk data" },
            new() { Name = "metadata", Type = "map<string,string>", Description = "Object metadata" },
            new() { Name = "version", Type = "int64", Description = "Object version" },
            new() { Name = "content_type", Type = "string", Nullable = true, Description = "Content type" },
            new() { Name = "total_size_bytes", Type = "int64", Description = "Total object size" }
        }
    };

    private static readonly SdkType QueryRequestType = new()
    {
        Name = "QueryRequest",
        Fields = new SdkField[]
        {
            new() { Name = "sql", Type = "string", Description = "SQL-like query string" },
            new() { Name = "parameters", Type = "map<string,string>", Description = "Query parameters" },
            new() { Name = "max_results", Type = "int32", Description = "Max result rows, 0 = unlimited" },
            new() { Name = "timeout_ms", Type = "int32", Description = "Timeout in milliseconds" }
        }
    };

    private static readonly SdkType QueryResultType = new()
    {
        Name = "QueryResult",
        Fields = new SdkField[]
        {
            new() { Name = "columns", Type = "list<ColumnDescriptor>", Description = "Column descriptors" },
            new() { Name = "rows", Type = "list<Row>", Description = "Result rows" },
            new() { Name = "total_rows", Type = "int64", Description = "Total row count" },
            new() { Name = "is_last", Type = "bool", Description = "Whether this is the last batch" }
        }
    };

    private static readonly SdkType TagRequestType = new()
    {
        Name = "TagRequest",
        Fields = new SdkField[]
        {
            new() { Name = "key", Type = "string", Description = "Object key" },
            new() { Name = "tags", Type = "map<string,string>", Description = "Tags to set" }
        }
    };

    private static readonly SdkType TagResponseType = new()
    {
        Name = "TagResponse",
        Fields = new SdkField[]
        {
            new() { Name = "key", Type = "string", Description = "Object key" },
            new() { Name = "tags", Type = "map<string,string>", Description = "Current tags" },
            new() { Name = "version", Type = "int64", Description = "Tag version" }
        }
    };

    private static readonly SdkType SearchRequestType = new()
    {
        Name = "SearchRequest",
        Fields = new SdkField[]
        {
            new() { Name = "query", Type = "string", Description = "Full-text search query" },
            new() { Name = "max_results", Type = "int32", Description = "Max results" },
            new() { Name = "offset", Type = "int32", Description = "Offset for pagination" },
            new() { Name = "filter", Type = "string", Nullable = true, Description = "Filter expression" },
            new() { Name = "sort_by", Type = "string", Description = "Sort order" }
        }
    };

    private static readonly SdkType SearchResultType = new()
    {
        Name = "SearchResult",
        Fields = new SdkField[]
        {
            new() { Name = "key", Type = "string", Description = "Object key" },
            new() { Name = "score", Type = "float64", Description = "Relevance score 0.0-1.0" },
            new() { Name = "metadata", Type = "map<string,string>", Description = "Object metadata" },
            new() { Name = "snippet", Type = "string", Nullable = true, Description = "Content snippet" },
            new() { Name = "size_bytes", Type = "int64", Description = "Object size" }
        }
    };

    private static readonly SdkType StreamStoreRequestType = new()
    {
        Name = "StreamStoreRequest",
        Fields = new SdkField[]
        {
            new() { Name = "key", Type = "string", Description = "Object key" },
            new() { Name = "data_stream", Type = "stream<bytes>", Description = "Data stream for upload" },
            new() { Name = "metadata", Type = "map<string,string>", Description = "Object metadata" }
        }
    };

    private static readonly SdkType SubscribeRequestType = new()
    {
        Name = "SubscribeRequest",
        Fields = new SdkField[]
        {
            new() { Name = "topic", Type = "string", Description = "Event topic to subscribe to" },
            new() { Name = "filter", Type = "string", Nullable = true, Description = "Event filter expression" }
        }
    };

    private static readonly SdkType EventType = new()
    {
        Name = "Event",
        Fields = new SdkField[]
        {
            new() { Name = "id", Type = "string", Description = "Event identifier" },
            new() { Name = "topic", Type = "string", Description = "Event topic" },
            new() { Name = "payload", Type = "bytes", Description = "Event payload" },
            new() { Name = "timestamp", Type = "datetime", Description = "Event timestamp" },
            new() { Name = "metadata", Type = "map<string,string>", Description = "Event metadata" }
        }
    };

    private static readonly SdkType HealthResponseType = new()
    {
        Name = "HealthStatus",
        Fields = new SdkField[]
        {
            new() { Name = "status", Type = "HealthState", Description = "Overall health status" },
            new() { Name = "uptime_seconds", Type = "int64", Description = "Instance uptime" },
            new() { Name = "version", Type = "string", Description = "Server version" },
            new() { Name = "components", Type = "map<string,ComponentHealth>", Description = "Per-component health" }
        }
    };

    private static readonly SdkType CapabilitiesResponseType = new()
    {
        Name = "CapabilityList",
        Fields = new SdkField[]
        {
            new() { Name = "capabilities", Type = "list<Capability>", Description = "Available capabilities" },
            new() { Name = "plugin_count", Type = "int32", Description = "Total plugin count" },
            new() { Name = "strategy_count", Type = "int32", Description = "Total strategy count" }
        }
    };

    // -------------------------------------------------------------------------
    // Build API Surface
    // -------------------------------------------------------------------------

    /// <summary>
    /// Constructs the full SDK API surface from the proto service definitions,
    /// mapping each RPC method to a canonical SDK method with idiomatic naming.
    /// </summary>
    public static SdkApiSurface BuildApiSurface()
    {
        var methods = new List<SdkMethod>
        {
            new()
            {
                Name = "connect",
                Description = "Establish a connection to the DataWarehouse server",
                ServiceName = "AdminService",
                RpcMethod = "GetHealth",
                InputType = ConnectionOptionsType,
                OutputType = ConnectionHandleType
            },
            new()
            {
                Name = "store",
                Description = "Store an object with key, data, metadata, and tags",
                ServiceName = "StorageService",
                RpcMethod = "Store",
                InputType = StoreRequestType,
                OutputType = StoreResponseType
            },
            new()
            {
                Name = "retrieve",
                Description = "Retrieve an object by key, returning a data stream",
                ServiceName = "StorageService",
                RpcMethod = "Retrieve",
                InputType = RetrieveRequestType,
                OutputType = DataStreamType,
                IsStreaming = true
            },
            new()
            {
                Name = "query",
                Description = "Execute a SQL query, returning result batches as an iterator",
                ServiceName = "QueryService",
                RpcMethod = "Execute",
                InputType = QueryRequestType,
                OutputType = QueryResultType,
                IsStreaming = true
            },
            new()
            {
                Name = "tag",
                Description = "Set tags on an object identified by key",
                ServiceName = "TagService",
                RpcMethod = "SetTags",
                InputType = TagRequestType,
                OutputType = TagResponseType
            },
            new()
            {
                Name = "search",
                Description = "Full-text search with optional filters, returning results as an iterator",
                ServiceName = "SearchService",
                RpcMethod = "Search",
                InputType = SearchRequestType,
                OutputType = SearchResultType,
                IsStreaming = true
            },
            new()
            {
                Name = "stream_store",
                Description = "Store an object via streaming upload for large payloads",
                ServiceName = "StreamService",
                RpcMethod = "StreamStore",
                InputType = StreamStoreRequestType,
                OutputType = StoreResponseType,
                IsClientStreaming = true
            },
            new()
            {
                Name = "subscribe",
                Description = "Subscribe to an event topic with optional filter, receiving an event stream",
                ServiceName = "StreamService",
                RpcMethod = "Subscribe",
                InputType = SubscribeRequestType,
                OutputType = EventType,
                IsStreaming = true
            },
            new()
            {
                Name = "health",
                Description = "Check server health status",
                ServiceName = "AdminService",
                RpcMethod = "GetHealth",
                InputType = new SdkType { Name = "Empty", Fields = Array.Empty<SdkField>() },
                OutputType = HealthResponseType
            },
            new()
            {
                Name = "capabilities",
                Description = "List available server capabilities and feature set",
                ServiceName = "AdminService",
                RpcMethod = "GetCapabilities",
                InputType = new SdkType { Name = "Empty", Fields = Array.Empty<SdkField>() },
                OutputType = CapabilitiesResponseType
            }
        };

        var types = new List<SdkType>
        {
            ConnectionOptionsType,
            ConnectionHandleType,
            StoreRequestType,
            StoreResponseType,
            RetrieveRequestType,
            DataStreamType,
            QueryRequestType,
            QueryResultType,
            TagRequestType,
            TagResponseType,
            SearchRequestType,
            SearchResultType,
            StreamStoreRequestType,
            SubscribeRequestType,
            EventType,
            HealthResponseType,
            CapabilitiesResponseType,
            new()
            {
                Name = "ColumnDescriptor",
                Fields = new SdkField[]
                {
                    new() { Name = "name", Type = "string", Description = "Column name" },
                    new() { Name = "data_type", Type = "ColumnType", Description = "Column data type" },
                    new() { Name = "nullable", Type = "bool", Description = "Whether the column is nullable" }
                }
            },
            new()
            {
                Name = "Row",
                Fields = new SdkField[]
                {
                    new() { Name = "values", Type = "list<bytes>", Description = "Serialized column values" },
                    new() { Name = "null_bitmap", Type = "bytes", Description = "Null bitmap" }
                }
            },
            new()
            {
                Name = "Capability",
                Fields = new SdkField[]
                {
                    new() { Name = "id", Type = "string", Description = "Capability ID" },
                    new() { Name = "display_name", Type = "string", Description = "Display name" },
                    new() { Name = "description", Type = "string", Description = "Description" },
                    new() { Name = "category", Type = "string", Description = "Category" },
                    new() { Name = "is_available", Type = "bool", Description = "Whether available" },
                    new() { Name = "tags", Type = "list<string>", Description = "Discovery tags" }
                }
            },
            new()
            {
                Name = "ComponentHealth",
                Fields = new SdkField[]
                {
                    new() { Name = "name", Type = "string", Description = "Component name" },
                    new() { Name = "status", Type = "HealthState", Description = "Health status" },
                    new() { Name = "message", Type = "string", Description = "Status message" }
                }
            }
        };

        var enums = new List<SdkEnum>
        {
            new()
            {
                Name = "HealthState",
                Values = new (string, int)[]
                {
                    ("Healthy", 0),
                    ("Degraded", 1),
                    ("Unhealthy", 2)
                }
            },
            new()
            {
                Name = "ColumnType",
                Values = new (string, int)[]
                {
                    ("Int32", 0),
                    ("Int64", 1),
                    ("Float64", 2),
                    ("String", 3),
                    ("Bool", 4),
                    ("Binary", 5),
                    ("Decimal", 6),
                    ("DateTime", 7),
                    ("Null", 8)
                }
            }
        };

        return new SdkApiSurface
        {
            Methods = methods,
            Types = types,
            Enums = enums
        };
    }

    // -------------------------------------------------------------------------
    // Language Bindings
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns language binding configuration for the specified language, including
    /// package metadata and language-specific idioms.
    /// </summary>
    public static SdkLanguageBinding GetLanguageBinding(SdkLanguage language) => language switch
    {
        SdkLanguage.Python => new SdkLanguageBinding
        {
            Language = SdkLanguage.Python,
            PackageName = "datawarehouse",
            Idioms = new SdkIdiom[]
            {
                new() { Pattern = "context_manager", Description = "Use 'with dw.connect() as client:' for automatic connection lifecycle" },
                new() { Pattern = "generator_streaming", Description = "Streaming RPCs return Python generators for lazy iteration" },
                new() { Pattern = "type_hints", Description = "Full PEP 484 type annotations on all public methods and classes" },
                new() { Pattern = "pandas_integration", Description = "QueryResult includes to_dataframe() for pandas DataFrame conversion" },
                new() { Pattern = "dataclass_types", Description = "SDK types are @dataclass frozen classes with __slots__" }
            }
        },
        SdkLanguage.Java => new SdkLanguageBinding
        {
            Language = SdkLanguage.Java,
            PackageName = "com.datawarehouse.sdk",
            Idioms = new SdkIdiom[]
            {
                new() { Pattern = "builder_pattern", Description = "Request types use Builder pattern for construction" },
                new() { Pattern = "completable_future", Description = "Async operations return CompletableFuture<T>" },
                new() { Pattern = "auto_closeable", Description = "Client implements AutoCloseable for try-with-resources" },
                new() { Pattern = "maven_coordinates", Description = "Published as com.datawarehouse:datawarehouse-sdk:1.0.0" },
                new() { Pattern = "jdbc_driver", Description = "Includes DataWarehouseJdbcDriver wrapping PostgreSQL wire protocol" }
            }
        },
        SdkLanguage.Go => new SdkLanguageBinding
        {
            Language = SdkLanguage.Go,
            PackageName = "github.com/datawarehouse/datawarehouse-go",
            Idioms = new SdkIdiom[]
            {
                new() { Pattern = "context_first_param", Description = "All methods take ctx context.Context as first parameter" },
                new() { Pattern = "error_returns", Description = "All methods return (T, error) — no exceptions" },
                new() { Pattern = "module_path", Description = "Go module path: github.com/datawarehouse/datawarehouse-go" },
                new() { Pattern = "functional_options", Description = "Configuration via functional options pattern (WithTLS, WithTimeout, etc.)" },
                new() { Pattern = "interface_contracts", Description = "Client exposes interfaces for testability and mocking" }
            }
        },
        SdkLanguage.Rust => new SdkLanguageBinding
        {
            Language = SdkLanguage.Rust,
            PackageName = "datawarehouse",
            Idioms = new SdkIdiom[]
            {
                new() { Pattern = "result_type", Description = "All methods return Result<T, DataWarehouseError>" },
                new() { Pattern = "impl_drop", Description = "Client implements Drop for automatic cleanup" },
                new() { Pattern = "tokio_async", Description = "Async runtime via tokio::spawn for concurrent operations" },
                new() { Pattern = "crate_name", Description = "Published as crate 'datawarehouse' on crates.io" },
                new() { Pattern = "serde_derive", Description = "Types derive Serialize/Deserialize for JSON interop" }
            }
        },
        SdkLanguage.TypeScript => new SdkLanguageBinding
        {
            Language = SdkLanguage.TypeScript,
            PackageName = "@datawarehouse/sdk",
            Idioms = new SdkIdiom[]
            {
                new() { Pattern = "promise_async", Description = "All methods return Promise<T> with async/await" },
                new() { Pattern = "typed_generics", Description = "Full TypeScript generics and strict null checks" },
                new() { Pattern = "npm_package", Description = "Published as @datawarehouse/sdk on npm" },
                new() { Pattern = "dual_output", Description = "ESM + CJS dual output for broad compatibility" },
                new() { Pattern = "dual_target", Description = "Supports both Node.js (grpc-js) and browser (grpc-web) targets" }
            }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, "Unsupported SDK language")
    };

    /// <summary>
    /// Returns language bindings for all supported languages.
    /// </summary>
    public static IReadOnlyList<SdkLanguageBinding> GetAllLanguageBindings()
    {
        return new[]
        {
            GetLanguageBinding(SdkLanguage.Python),
            GetLanguageBinding(SdkLanguage.Java),
            GetLanguageBinding(SdkLanguage.Go),
            GetLanguageBinding(SdkLanguage.Rust),
            GetLanguageBinding(SdkLanguage.TypeScript)
        };
    }
}
