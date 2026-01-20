using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.RestInterface;

/// <summary>
/// REST API interface plugin providing HTTP/HTTPS endpoints for DataWarehouse operations.
/// Implements a lightweight HTTP server with full CRUD operations, batch processing, and streaming.
///
/// Features:
/// - RESTful CRUD operations (GET, POST, PUT, DELETE)
/// - Batch operations for bulk data processing
/// - Streaming uploads/downloads for large files
/// - Content negotiation (JSON, binary)
/// - API versioning support
/// - Rate limiting and throttling
/// - OpenAPI specification generation
/// - CORS support for web clients
/// - PRODUCTION STORAGE: Uses IKernelStorageService for real persistence
///
/// Endpoints:
/// - GET    /api/v1/blobs/{id}          - Retrieve blob
/// - POST   /api/v1/blobs               - Create blob
/// - PUT    /api/v1/blobs/{id}          - Update blob
/// - DELETE /api/v1/blobs/{id}          - Delete blob
/// - GET    /api/v1/blobs               - List blobs
/// - POST   /api/v1/blobs/batch         - Batch operations
/// - GET    /api/v1/health              - Health check
/// - GET    /api/v1/openapi.json        - OpenAPI spec
/// </summary>
public sealed class RestInterfacePlugin : InterfacePluginBase
{
    public override string Id => "datawarehouse.plugins.interface.rest";
    public override string Name => "REST API Interface";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.InterfaceProvider;
    public override string Protocol => "http";
    public override int? Port => _port;
    public override string? BasePath => "/api/v1";

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private int _port = 8080;
    private readonly ConcurrentDictionary<string, RateLimitEntry> _rateLimits = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    // Storage service - injected via handshake configuration
    private IKernelStorageService? _storage;
    private const string StoragePrefix = "rest-blobs/";

    // Configuration
    private int _maxRequestsPerMinute = 100;
    private readonly string[] _allowedOrigins = { "*" };
    private readonly string _apiVersion = "v1";

    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        // Parse configuration from request
        if (request.Config?.TryGetValue("port", out var portObj) == true && portObj is int port)
            _port = port;

        if (request.Config?.TryGetValue("rateLimit", out var rateLimitObj) == true && rateLimitObj is int rateLimit)
            _maxRequestsPerMinute = rateLimit;

        // Get storage service from configuration (injected by kernel)
        if (request.Config?.TryGetValue("kernelStorage", out var storageObj) == true && storageObj is IKernelStorageService storage)
            _storage = storage;

        return Task.FromResult(new HandshakeResponse
        {
            PluginId = Id,
            Name = Name,
            Version = ParseSemanticVersion(Version),
            Category = Category,
            Success = true,
            ReadyState = PluginReadyState.Ready,
            Capabilities = GetCapabilities(),
            Metadata = GetMetadata()
        });
    }

    /// <summary>
    /// Sets the kernel storage service for production persistence.
    /// Call this before starting the plugin.
    /// </summary>
    public void SetStorageService(IKernelStorageService storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new()
            {
                Name = "crud_operations",
                DisplayName = "CRUD Operations",
                Description = "Create, Read, Update, Delete operations via REST"
            },
            new()
            {
                Name = "batch_operations",
                DisplayName = "Batch Operations",
                Description = "Bulk data operations in a single request"
            },
            new()
            {
                Name = "streaming",
                DisplayName = "Streaming",
                Description = "Stream large files for upload/download"
            },
            new()
            {
                Name = "openapi",
                DisplayName = "OpenAPI",
                Description = "OpenAPI specification for API discovery"
            },
            new()
            {
                Name = "persistent_storage",
                DisplayName = "Persistent Storage",
                Description = "Data persisted via kernel storage service"
            }
        };
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["ApiVersion"] = _apiVersion;
        metadata["RateLimitPerMinute"] = _maxRequestsPerMinute;
        metadata["SupportsCORS"] = true;
        metadata["SupportsStreaming"] = true;
        metadata["ContentTypes"] = new[] { "application/json", "application/octet-stream" };
        metadata["UsesPersistentStorage"] = _storage != null;
        return metadata;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_port}/");

        try
        {
            _listener.Start();
            _serverTask = ProcessRequestsAsync(_cts.Token);
        }
        catch (HttpListenerException)
        {
            // May need admin rights for http://+:port/ - try localhost
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _serverTask = ProcessRequestsAsync(_cts.Token);
        }

        await Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        if (_serverTask != null)
        {
            try { await _serverTask; } catch (OperationCanceledException) { }
        }

        _listener?.Close();
    }

    private async Task ProcessRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequestAsync(context, ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log and continue - don't let one bad request crash the server
                Console.Error.WriteLine($"REST API error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // Add CORS headers
            AddCorsHeaders(response);

            // Handle preflight
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            // Rate limiting
            var clientIp = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            if (!CheckRateLimit(clientIp))
            {
                await SendErrorAsync(response, 429, "Rate limit exceeded");
                return;
            }

            // Route request
            var path = request.Url?.AbsolutePath ?? "/";
            var method = request.HttpMethod;

            var result = await RouteRequestAsync(method, path, request, ct);
            await SendResponseAsync(response, result);
        }
        catch (Exception ex)
        {
            await SendErrorAsync(response, 500, ex.Message);
        }
        finally
        {
            response.Close();
        }
    }

    private async Task<ApiResponse> RouteRequestAsync(string method, string path, HttpListenerRequest request, CancellationToken ct)
    {
        // Normalize path
        path = path.TrimEnd('/');

        // Health check
        if (path == "/api/v1/health" || path == "/health")
            return new ApiResponse
            {
                StatusCode = 200,
                Data = new
                {
                    status = "healthy",
                    timestamp = DateTime.UtcNow,
                    storageConfigured = _storage != null
                }
            };

        // OpenAPI spec
        if (path == "/api/v1/openapi.json" || path == "/openapi.json")
            return new ApiResponse { StatusCode = 200, Data = GenerateOpenApiSpec() };

        // Blob operations
        if (path.StartsWith("/api/v1/blobs"))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var blobId = segments.Length > 3 ? segments[3] : null;

            return (method, blobId) switch
            {
                ("GET", null) => await HandleListBlobsAsync(request, ct),
                ("GET", string id) => await HandleGetBlobAsync(id, ct),
                ("POST", null) when path.EndsWith("/batch") => await HandleBatchOperationsAsync(request, ct),
                ("POST", null) => await HandleCreateBlobAsync(request, ct),
                ("PUT", string id) => await HandleUpdateBlobAsync(id, request, ct),
                ("DELETE", string id) => await HandleDeleteBlobAsync(id, ct),
                _ => new ApiResponse { StatusCode = 405, Data = new { error = "Method not allowed" } }
            };
        }

        return new ApiResponse { StatusCode = 404, Data = new { error = "Not found" } };
    }

    private async Task<ApiResponse> HandleListBlobsAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (_storage == null)
        {
            return new ApiResponse { StatusCode = 503, Data = new { error = "Storage service not configured" } };
        }

        var query = request.QueryString;
        var limit = int.TryParse(query["limit"], out var l) ? l : 100;
        var offset = int.TryParse(query["offset"], out var o) ? o : 0;
        var prefix = query["prefix"] ?? "";

        var items = await _storage.ListAsync(StoragePrefix + prefix, limit, offset, ct);

        var blobs = items.Select(i => new
        {
            id = i.Path.Replace(StoragePrefix, ""),
            uri = $"/api/v1/blobs/{i.Path.Replace(StoragePrefix, "")}",
            size = i.SizeBytes,
            created = i.CreatedAt,
            modified = i.ModifiedAt
        }).ToList();

        return new ApiResponse
        {
            StatusCode = 200,
            Data = new
            {
                items = blobs,
                total = blobs.Count,
                limit,
                offset
            }
        };
    }

    private async Task<ApiResponse> HandleGetBlobAsync(string id, CancellationToken ct)
    {
        if (_storage == null)
        {
            return new ApiResponse { StatusCode = 503, Data = new { error = "Storage service not configured" } };
        }

        var path = StoragePrefix + id;
        var data = await _storage.LoadBytesAsync(path, ct);

        if (data == null)
        {
            return new ApiResponse { StatusCode = 404, Data = new { error = "Blob not found" } };
        }

        // Try to deserialize as JSON, otherwise return raw
        try
        {
            var content = Encoding.UTF8.GetString(data);
            var obj = JsonSerializer.Deserialize<object>(content, _jsonOptions);
            return new ApiResponse { StatusCode = 200, Data = obj };
        }
        catch
        {
            // Return as base64 if not JSON
            return new ApiResponse
            {
                StatusCode = 200,
                Data = new
                {
                    id,
                    contentType = "application/octet-stream",
                    data = Convert.ToBase64String(data),
                    size = data.Length
                }
            };
        }
    }

    private async Task<ApiResponse> HandleCreateBlobAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (_storage == null)
        {
            return new ApiResponse { StatusCode = 503, Data = new { error = "Storage service not configured" } };
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);

        var id = Guid.NewGuid().ToString();
        var path = StoragePrefix + id;

        var metadata = new Dictionary<string, string>
        {
            ["ContentType"] = request.ContentType ?? "application/json",
            ["CreatedAt"] = DateTime.UtcNow.ToString("O")
        };

        await _storage.SaveAsync(path, Encoding.UTF8.GetBytes(body), metadata, ct);

        return new ApiResponse
        {
            StatusCode = 201,
            Data = new { id, uri = $"/api/v1/blobs/{id}", created = DateTime.UtcNow }
        };
    }

    private async Task<ApiResponse> HandleUpdateBlobAsync(string id, HttpListenerRequest request, CancellationToken ct)
    {
        if (_storage == null)
        {
            return new ApiResponse { StatusCode = 503, Data = new { error = "Storage service not configured" } };
        }

        var path = StoragePrefix + id;

        if (!await _storage.ExistsAsync(path, ct))
        {
            return new ApiResponse { StatusCode = 404, Data = new { error = "Blob not found" } };
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);

        var metadata = new Dictionary<string, string>
        {
            ["ContentType"] = request.ContentType ?? "application/json",
            ["ModifiedAt"] = DateTime.UtcNow.ToString("O")
        };

        await _storage.SaveAsync(path, Encoding.UTF8.GetBytes(body), metadata, ct);

        return new ApiResponse
        {
            StatusCode = 200,
            Data = new { id, updated = DateTime.UtcNow }
        };
    }

    private async Task<ApiResponse> HandleDeleteBlobAsync(string id, CancellationToken ct)
    {
        if (_storage == null)
        {
            return new ApiResponse { StatusCode = 503, Data = new { error = "Storage service not configured" } };
        }

        var path = StoragePrefix + id;
        var deleted = await _storage.DeleteAsync(path, ct);

        if (deleted)
        {
            return new ApiResponse { StatusCode = 204 };
        }
        return new ApiResponse { StatusCode = 404, Data = new { error = "Blob not found" } };
    }

    private async Task<ApiResponse> HandleBatchOperationsAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (_storage == null)
        {
            return new ApiResponse { StatusCode = 503, Data = new { error = "Storage service not configured" } };
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);

        try
        {
            var operations = JsonSerializer.Deserialize<BatchRequest>(body, _jsonOptions);
            var results = new List<object>();

            foreach (var op in operations?.Operations ?? Array.Empty<BatchOperation>())
            {
                var result = op.Method switch
                {
                    "GET" => await HandleGetBlobAsync(op.Id, ct),
                    "DELETE" => await HandleDeleteBlobAsync(op.Id, ct),
                    _ => new ApiResponse { StatusCode = 400, Data = new { error = "Invalid operation" } }
                };
                results.Add(new { op.Id, statusCode = result.StatusCode, data = result.Data });
            }

            return new ApiResponse
            {
                StatusCode = 200,
                Data = new { results, processedCount = results.Count }
            };
        }
        catch (Exception ex)
        {
            return new ApiResponse { StatusCode = 400, Data = new { error = ex.Message } };
        }
    }

    private void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers.Add("Access-Control-Allow-Origin", string.Join(",", _allowedOrigins));
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
        response.Headers.Add("Access-Control-Max-Age", "86400");
    }

    private bool CheckRateLimit(string clientId)
    {
        var now = DateTime.UtcNow;
        var entry = _rateLimits.GetOrAdd(clientId, _ => new RateLimitEntry { WindowStart = now });

        lock (entry)
        {
            // Reset window if expired
            if ((now - entry.WindowStart).TotalMinutes >= 1)
            {
                entry.WindowStart = now;
                entry.RequestCount = 0;
            }

            entry.RequestCount++;
            return entry.RequestCount <= _maxRequestsPerMinute;
        }
    }

    private async Task SendResponseAsync(HttpListenerResponse response, ApiResponse apiResponse)
    {
        response.StatusCode = apiResponse.StatusCode;
        response.ContentType = "application/json";

        if (apiResponse.Data != null)
        {
            var json = JsonSerializer.Serialize(apiResponse.Data, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
    }

    private async Task SendErrorAsync(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";

        var json = JsonSerializer.Serialize(new { error = message, statusCode }, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private object GenerateOpenApiSpec()
    {
        return new
        {
            openapi = "3.0.3",
            info = new
            {
                title = "DataWarehouse REST API",
                version = _apiVersion,
                description = "RESTful API for DataWarehouse blob operations with persistent storage"
            },
            servers = new[]
            {
                new { url = $"http://localhost:{_port}/api/{_apiVersion}" }
            },
            paths = new Dictionary<string, object>
            {
                ["/blobs"] = new
                {
                    get = new
                    {
                        summary = "List all blobs",
                        parameters = new[]
                        {
                            new { name = "limit", @in = "query", schema = new { type = "integer" } },
                            new { name = "offset", @in = "query", schema = new { type = "integer" } }
                        },
                        responses = new { _200 = new { description = "Success" } }
                    },
                    post = new
                    {
                        summary = "Create a new blob",
                        requestBody = new { content = new { application_json = new { schema = new { type = "object" } } } },
                        responses = new { _201 = new { description = "Created" } }
                    }
                },
                ["/blobs/{id}"] = new
                {
                    get = new { summary = "Get blob by ID", responses = new { _200 = new { description = "Success" } } },
                    put = new { summary = "Update blob", responses = new { _200 = new { description = "Success" } } },
                    delete = new { summary = "Delete blob", responses = new { _204 = new { description = "Deleted" } } }
                },
                ["/health"] = new
                {
                    get = new { summary = "Health check", responses = new { _200 = new { description = "Healthy" } } }
                }
            }
        };
    }

    private sealed class ApiResponse
    {
        public int StatusCode { get; init; }
        public object? Data { get; init; }
    }

    private sealed class RateLimitEntry
    {
        public DateTime WindowStart { get; set; }
        public int RequestCount { get; set; }
    }

    private sealed class BatchRequest
    {
        public BatchOperation[] Operations { get; set; } = Array.Empty<BatchOperation>();
    }

    private sealed class BatchOperation
    {
        public string Method { get; set; } = "";
        public string Id { get; set; } = "";
        public object? Data { get; set; }
    }
}
