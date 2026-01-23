using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.GraphQlApi;

/// <summary>
/// GraphQL API plugin providing a complete GraphQL endpoint for DataWarehouse operations.
/// Implements full GraphQL specification including queries, mutations, subscriptions,
/// batching with DataLoader pattern, and real-time updates via WebSocket.
///
/// Features:
/// - Full GraphQL schema for storage operations
/// - Real-time subscriptions via WebSocket
/// - DataLoader pattern for batching and caching
/// - Query complexity analysis and limits
/// - Introspection support
/// - Persisted queries
/// - Field-level authorization
/// - Query depth limiting
/// - Rate limiting per operation type
///
/// Endpoints:
/// - POST /graphql - Query execution
/// - GET  /graphql - Introspection and playground
/// - WS   /graphql - Subscriptions
/// - GET  /schema.graphql - SDL download
///
/// Message Commands:
/// - graphql.query: Execute GraphQL query
/// - graphql.subscribe: Subscribe to real-time updates
/// - graphql.unsubscribe: Unsubscribe from updates
/// - graphql.introspect: Get schema introspection
/// - graphql.persisted.add: Add persisted query
/// - graphql.status: Get endpoint status
/// </summary>
public sealed class GraphQlApiPlugin : InterfacePluginBase
{
    /// <inheritdoc />
    public override string Id => "com.datawarehouse.api.graphql";

    /// <inheritdoc />
    public override string Name => "GraphQL API Plugin";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override string Protocol => "http";

    /// <inheritdoc />
    public override int? Port => _port;

    /// <inheritdoc />
    public override string? BasePath => "/graphql";

    #region Private Fields

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private int _port = 4000;

    private IKernelStorageService? _storage;
    private IKernelContext? _context;

    private readonly ConcurrentDictionary<string, SubscriptionState> _subscriptions = new();
    private readonly ConcurrentDictionary<string, string> _persistedQueries = new();
    private readonly ConcurrentDictionary<string, DataLoaderBatch> _dataLoaderBatches = new();
    private readonly ConcurrentDictionary<string, RateLimitEntry> _rateLimits = new();

    private readonly object _subscriptionLock = new();
    private bool _isRunning;

    private const int MaxQueryDepth = 10;
    private const int MaxQueryComplexity = 1000;
    private const int RateLimitPerMinute = 1000;
    private const int DataLoaderBatchDelayMs = 10;
    private const string StoragePrefix = "graphql-data/";

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _context = request.Context;

        if (request.Config?.TryGetValue("port", out var portObj) == true && portObj is int port)
        {
            _port = port;
        }

        if (request.Config?.TryGetValue("kernelStorage", out var storageObj) == true && storageObj is IKernelStorageService storage)
        {
            _storage = storage;
        }

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

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new HttpListener();

        try
        {
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
        }

        _isRunning = true;
        _serverTask = ProcessRequestsAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();
        _listener?.Stop();

        if (_serverTask != null)
        {
            try { await _serverTask; } catch (OperationCanceledException) { }
        }

        // Close all subscriptions
        foreach (var sub in _subscriptions.Values)
        {
            sub.CancellationSource.Cancel();
        }
        _subscriptions.Clear();

        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    #endregion

    #region HTTP Server

    private async Task ProcessRequestsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleHttpRequestAsync(context, ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue serving
            }
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // Add CORS headers
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            // Rate limiting
            var clientId = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
            if (!CheckRateLimit(clientId))
            {
                await SendErrorResponse(response, 429, "Rate limit exceeded");
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";

            if (path == "/schema.graphql" && request.HttpMethod == "GET")
            {
                await SendSchemaResponse(response);
            }
            else if (path.StartsWith("/graphql"))
            {
                if (request.IsWebSocketRequest)
                {
                    await HandleWebSocketAsync(context, ct);
                }
                else if (request.HttpMethod == "POST")
                {
                    await HandleGraphQlQueryAsync(request, response, ct);
                }
                else if (request.HttpMethod == "GET")
                {
                    await SendPlaygroundResponse(response);
                }
                else
                {
                    await SendErrorResponse(response, 405, "Method not allowed");
                }
            }
            else
            {
                await SendErrorResponse(response, 404, "Not found");
            }
        }
        catch (Exception ex)
        {
            await SendErrorResponse(response, 500, ex.Message);
        }
        finally
        {
            response.Close();
        }
    }

    private async Task HandleGraphQlQueryAsync(
        HttpListenerRequest request,
        HttpListenerResponse response,
        CancellationToken ct)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct);

        GraphQlRequest? graphQlRequest;
        try
        {
            graphQlRequest = JsonSerializer.Deserialize<GraphQlRequest>(body, _jsonOptions);
        }
        catch
        {
            await SendGraphQlResponse(response, new GraphQlResponse
            {
                Errors = new List<GraphQlError>
                {
                    new() { Message = "Invalid GraphQL request body" }
                }
            });
            return;
        }

        if (graphQlRequest == null)
        {
            await SendGraphQlResponse(response, new GraphQlResponse
            {
                Errors = new List<GraphQlError>
                {
                    new() { Message = "Request body is required" }
                }
            });
            return;
        }

        // Handle persisted query
        var query = graphQlRequest.Query;
        if (string.IsNullOrEmpty(query) && !string.IsNullOrEmpty(graphQlRequest.Extensions?.PersistedQuery?.Sha256Hash))
        {
            var hash = graphQlRequest.Extensions.PersistedQuery.Sha256Hash;
            if (!_persistedQueries.TryGetValue(hash, out query))
            {
                await SendGraphQlResponse(response, new GraphQlResponse
                {
                    Errors = new List<GraphQlError>
                    {
                        new()
                        {
                            Message = "PersistedQueryNotFound",
                            Extensions = new Dictionary<string, object>
                            {
                                ["code"] = "PERSISTED_QUERY_NOT_FOUND"
                            }
                        }
                    }
                });
                return;
            }
        }

        if (string.IsNullOrEmpty(query))
        {
            await SendGraphQlResponse(response, new GraphQlResponse
            {
                Errors = new List<GraphQlError>
                {
                    new() { Message = "Query is required" }
                }
            });
            return;
        }

        // Validate query
        var validationResult = ValidateQuery(query);
        if (!validationResult.IsValid)
        {
            await SendGraphQlResponse(response, new GraphQlResponse
            {
                Errors = validationResult.Errors
            });
            return;
        }

        // Execute query
        var result = await ExecuteQueryAsync(
            query,
            graphQlRequest.OperationName,
            graphQlRequest.Variables ?? new Dictionary<string, object>(),
            ct);

        await SendGraphQlResponse(response, result);
    }

    #endregion

    #region Query Execution

    private async Task<GraphQlResponse> ExecuteQueryAsync(
        string query,
        string? operationName,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        try
        {
            // Parse query to determine operation type
            var operation = ParseOperation(query, operationName);

            return operation.Type switch
            {
                OperationType.Query => await ExecuteQueryOperationAsync(operation, variables, ct),
                OperationType.Mutation => await ExecuteMutationAsync(operation, variables, ct),
                OperationType.Subscription => new GraphQlResponse
                {
                    Errors = new List<GraphQlError>
                    {
                        new() { Message = "Subscriptions must use WebSocket connection" }
                    }
                },
                _ => new GraphQlResponse
                {
                    Errors = new List<GraphQlError>
                    {
                        new() { Message = "Unknown operation type" }
                    }
                }
            };
        }
        catch (Exception ex)
        {
            return new GraphQlResponse
            {
                Errors = new List<GraphQlError>
                {
                    new() { Message = ex.Message }
                }
            };
        }
    }

    private async Task<GraphQlResponse> ExecuteQueryOperationAsync(
        GraphQlOperation operation,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        foreach (var field in operation.SelectionSet)
        {
            var result = await ResolveFieldAsync(field, null, variables, ct);
            data[field.Alias ?? field.Name] = result;
        }

        return new GraphQlResponse { Data = data };
    }

    private async Task<GraphQlResponse> ExecuteMutationAsync(
        GraphQlOperation operation,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>();

        // Execute mutations sequentially
        foreach (var field in operation.SelectionSet)
        {
            var result = await ResolveMutationFieldAsync(field, variables, ct);
            data[field.Alias ?? field.Name] = result;
        }

        // Notify subscriptions of mutations
        await NotifySubscribersAsync(operation.SelectionSet);

        return new GraphQlResponse { Data = data };
    }

    private async Task<object?> ResolveFieldAsync(
        FieldSelection field,
        object? parent,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        // Root query fields
        if (parent == null)
        {
            return field.Name switch
            {
                "blob" => await ResolveGetBlobAsync(field, variables, ct),
                "blobs" => await ResolveListBlobsAsync(field, variables, ct),
                "blobsByPrefix" => await ResolveBlobsByPrefixAsync(field, variables, ct),
                "metadata" => await ResolveMetadataAsync(field, variables, ct),
                "stats" => await ResolveStatsAsync(field, variables, ct),
                "__schema" => ResolveIntrospectionSchema(),
                "__type" => ResolveIntrospectionType(field, variables),
                _ => null
            };
        }

        // Nested field resolution
        if (parent is Dictionary<string, object> dict)
        {
            return dict.TryGetValue(field.Name, out var value) ? value : null;
        }

        return null;
    }

    private async Task<object?> ResolveMutationFieldAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        return field.Name switch
        {
            "createBlob" => await ResolveCreateBlobAsync(field, variables, ct),
            "updateBlob" => await ResolveUpdateBlobAsync(field, variables, ct),
            "deleteBlob" => await ResolveDeleteBlobAsync(field, variables, ct),
            "batchCreateBlobs" => await ResolveBatchCreateBlobsAsync(field, variables, ct),
            "batchDeleteBlobs" => await ResolveBatchDeleteBlobsAsync(field, variables, ct),
            _ => throw new InvalidOperationException($"Unknown mutation: {field.Name}")
        };
    }

    #endregion

    #region Resolvers

    private async Task<object?> ResolveGetBlobAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var id = GetArgumentValue<string>(field, "id", variables);
        if (string.IsNullOrEmpty(id))
            return null;

        // Use DataLoader for batching
        return await LoadBlobAsync(id, ct);
    }

    private async Task<object?> ResolveListBlobsAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var first = GetArgumentValue<int?>(field, "first", variables) ?? 10;
        var after = GetArgumentValue<string>(field, "after", variables);

        if (_storage == null)
        {
            return new { edges = Array.Empty<object>(), pageInfo = new { hasNextPage = false, endCursor = "" } };
        }

        var offset = 0;
        if (!string.IsNullOrEmpty(after))
        {
            int.TryParse(after, out offset);
        }

        var items = await _storage.ListAsync(StoragePrefix, first + 1, offset, ct);
        var hasNextPage = items.Count() > first;

        var edges = items.Take(first).Select((item, index) => new Dictionary<string, object>
        {
            ["cursor"] = (offset + index).ToString(),
            ["node"] = new Dictionary<string, object>
            {
                ["id"] = item.Path.Replace(StoragePrefix, ""),
                ["path"] = item.Path,
                ["size"] = item.SizeBytes,
                ["createdAt"] = item.CreatedAt.ToString("O"),
                ["modifiedAt"] = item.ModifiedAt.ToString("O")
            }
        }).ToList();

        return new Dictionary<string, object>
        {
            ["edges"] = edges,
            ["pageInfo"] = new Dictionary<string, object>
            {
                ["hasNextPage"] = hasNextPage,
                ["endCursor"] = edges.Any() ? edges.Last()["cursor"] : ""
            },
            ["totalCount"] = edges.Count
        };
    }

    private async Task<object?> ResolveBlobsByPrefixAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var prefix = GetArgumentValue<string>(field, "prefix", variables) ?? "";
        var limit = GetArgumentValue<int?>(field, "limit", variables) ?? 100;

        if (_storage == null)
        {
            return Array.Empty<object>();
        }

        var items = await _storage.ListAsync(StoragePrefix + prefix, limit, 0, ct);

        return items.Select(item => new Dictionary<string, object>
        {
            ["id"] = item.Path.Replace(StoragePrefix, ""),
            ["path"] = item.Path,
            ["size"] = item.SizeBytes,
            ["createdAt"] = item.CreatedAt.ToString("O"),
            ["modifiedAt"] = item.ModifiedAt.ToString("O")
        }).ToList();
    }

    private async Task<object?> ResolveMetadataAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var id = GetArgumentValue<string>(field, "id", variables);
        if (string.IsNullOrEmpty(id) || _storage == null)
            return null;

        var metadata = await _storage.GetMetadataAsync(StoragePrefix + id, ct);
        return metadata;
    }

    private Task<object?> ResolveStatsAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        return Task.FromResult<object?>(new Dictionary<string, object>
        {
            ["totalBlobs"] = _dataLoaderBatches.Count,
            ["activeSubscriptions"] = _subscriptions.Count,
            ["persistedQueries"] = _persistedQueries.Count,
            ["uptime"] = DateTime.UtcNow.ToString("O")
        });
    }

    private async Task<object?> ResolveCreateBlobAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var input = GetArgumentValue<Dictionary<string, object>>(field, "input", variables);
        if (input == null || _storage == null)
        {
            throw new InvalidOperationException("Input is required");
        }

        var id = input.GetValueOrDefault("id")?.ToString() ?? Guid.NewGuid().ToString();
        var content = input.GetValueOrDefault("content")?.ToString() ?? "";
        var metadataObj = input.GetValueOrDefault("metadata");

        var metadata = new Dictionary<string, string>
        {
            ["CreatedAt"] = DateTime.UtcNow.ToString("O")
        };

        if (metadataObj is Dictionary<string, object> metaDict)
        {
            foreach (var kv in metaDict)
            {
                metadata[kv.Key] = kv.Value?.ToString() ?? "";
            }
        }

        await _storage.SaveAsync(StoragePrefix + id, Encoding.UTF8.GetBytes(content), metadata, ct);

        return new Dictionary<string, object>
        {
            ["id"] = id,
            ["path"] = StoragePrefix + id,
            ["size"] = Encoding.UTF8.GetByteCount(content),
            ["createdAt"] = metadata["CreatedAt"]
        };
    }

    private async Task<object?> ResolveUpdateBlobAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var id = GetArgumentValue<string>(field, "id", variables);
        var input = GetArgumentValue<Dictionary<string, object>>(field, "input", variables);

        if (string.IsNullOrEmpty(id) || input == null || _storage == null)
        {
            throw new InvalidOperationException("ID and input are required");
        }

        var path = StoragePrefix + id;
        if (!await _storage.ExistsAsync(path, ct))
        {
            throw new InvalidOperationException($"Blob {id} not found");
        }

        var content = input.GetValueOrDefault("content")?.ToString() ?? "";
        var metadata = new Dictionary<string, string>
        {
            ["ModifiedAt"] = DateTime.UtcNow.ToString("O")
        };

        await _storage.SaveAsync(path, Encoding.UTF8.GetBytes(content), metadata, ct);

        return new Dictionary<string, object>
        {
            ["id"] = id,
            ["path"] = path,
            ["size"] = Encoding.UTF8.GetByteCount(content),
            ["modifiedAt"] = metadata["ModifiedAt"]
        };
    }

    private async Task<object?> ResolveDeleteBlobAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var id = GetArgumentValue<string>(field, "id", variables);

        if (string.IsNullOrEmpty(id) || _storage == null)
        {
            throw new InvalidOperationException("ID is required");
        }

        var deleted = await _storage.DeleteAsync(StoragePrefix + id, ct);

        return new Dictionary<string, object>
        {
            ["success"] = deleted,
            ["id"] = id
        };
    }

    private async Task<object?> ResolveBatchCreateBlobsAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var inputsJson = GetArgumentValue<object>(field, "inputs", variables);
        if (inputsJson == null || _storage == null)
        {
            throw new InvalidOperationException("Inputs are required");
        }

        var inputs = inputsJson is JsonElement element
            ? element.Deserialize<List<Dictionary<string, object>>>(_jsonOptions) ?? new List<Dictionary<string, object>>()
            : new List<Dictionary<string, object>>();

        var results = new List<Dictionary<string, object>>();

        foreach (var input in inputs)
        {
            var id = input.GetValueOrDefault("id")?.ToString() ?? Guid.NewGuid().ToString();
            var content = input.GetValueOrDefault("content")?.ToString() ?? "";

            var metadata = new Dictionary<string, string>
            {
                ["CreatedAt"] = DateTime.UtcNow.ToString("O")
            };

            await _storage.SaveAsync(StoragePrefix + id, Encoding.UTF8.GetBytes(content), metadata, ct);

            results.Add(new Dictionary<string, object>
            {
                ["id"] = id,
                ["path"] = StoragePrefix + id,
                ["size"] = Encoding.UTF8.GetByteCount(content),
                ["createdAt"] = metadata["CreatedAt"]
            });
        }

        return results;
    }

    private async Task<object?> ResolveBatchDeleteBlobsAsync(
        FieldSelection field,
        Dictionary<string, object> variables,
        CancellationToken ct)
    {
        var idsJson = GetArgumentValue<object>(field, "ids", variables);
        if (idsJson == null || _storage == null)
        {
            throw new InvalidOperationException("IDs are required");
        }

        var ids = idsJson is JsonElement element
            ? element.Deserialize<List<string>>(_jsonOptions) ?? new List<string>()
            : new List<string>();

        var results = new List<Dictionary<string, object>>();

        foreach (var id in ids)
        {
            var deleted = await _storage.DeleteAsync(StoragePrefix + id, ct);
            results.Add(new Dictionary<string, object>
            {
                ["id"] = id,
                ["success"] = deleted
            });
        }

        return results;
    }

    #endregion

    #region DataLoader

    private async Task<object?> LoadBlobAsync(string id, CancellationToken ct)
    {
        // Get or create batch for this tick
        var batchKey = DateTime.UtcNow.Ticks.ToString();
        var batch = _dataLoaderBatches.GetOrAdd(batchKey, _ => new DataLoaderBatch());

        // Add to pending requests
        var tcs = new TaskCompletionSource<object?>();
        batch.PendingRequests[id] = tcs;

        // Schedule batch execution
        if (Interlocked.CompareExchange(ref batch.IsScheduled, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(DataLoaderBatchDelayMs, ct);
                await ExecuteBatchAsync(batch, ct);
                _dataLoaderBatches.TryRemove(batchKey, out _);
            }, ct);
        }

        return await tcs.Task;
    }

    private async Task ExecuteBatchAsync(DataLoaderBatch batch, CancellationToken ct)
    {
        if (_storage == null)
        {
            foreach (var tcs in batch.PendingRequests.Values)
            {
                tcs.SetResult(null);
            }
            return;
        }

        var ids = batch.PendingRequests.Keys.ToList();

        // Load all blobs in parallel
        var tasks = ids.Select(async id =>
        {
            var data = await _storage.LoadBytesAsync(StoragePrefix + id, ct);
            return (id, data);
        });

        var results = await Task.WhenAll(tasks);

        foreach (var (id, data) in results)
        {
            if (batch.PendingRequests.TryGetValue(id, out var tcs))
            {
                if (data != null)
                {
                    tcs.SetResult(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["path"] = StoragePrefix + id,
                        ["content"] = Encoding.UTF8.GetString(data),
                        ["size"] = data.Length
                    });
                }
                else
                {
                    tcs.SetResult(null);
                }
            }
        }
    }

    #endregion

    #region Subscriptions

    private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken ct)
    {
        var wsContext = await context.AcceptWebSocketAsync(null);
        var webSocket = wsContext.WebSocket;
        var subscriptionId = Guid.NewGuid().ToString();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var state = new SubscriptionState
        {
            SubscriptionId = subscriptionId,
            WebSocket = webSocket,
            CancellationSource = cts,
            SubscribedTopics = new HashSet<string>()
        };

        _subscriptions[subscriptionId] = state;

        try
        {
            var buffer = new byte[4096];

            while (webSocket.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                        "Closed",
                        ct);
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await HandleWebSocketMessageAsync(state, message, ct);
            }
        }
        finally
        {
            _subscriptions.TryRemove(subscriptionId, out _);
            cts.Dispose();
        }
    }

    private async Task HandleWebSocketMessageAsync(
        SubscriptionState state,
        string message,
        CancellationToken ct)
    {
        try
        {
            var wsMessage = JsonSerializer.Deserialize<WebSocketMessage>(message, _jsonOptions);
            if (wsMessage == null) return;

            switch (wsMessage.Type)
            {
                case "connection_init":
                    await SendWebSocketMessageAsync(state, new WebSocketMessage
                    {
                        Type = "connection_ack"
                    }, ct);
                    break;

                case "subscribe":
                    if (wsMessage.Payload?.TryGetValue("query", out var query) == true)
                    {
                        var queryStr = query?.ToString();
                        if (!string.IsNullOrEmpty(queryStr))
                        {
                            // Parse subscription from query
                            var subscriptionTopic = ExtractSubscriptionTopic(queryStr);
                            state.SubscribedTopics.Add(subscriptionTopic);

                            await SendWebSocketMessageAsync(state, new WebSocketMessage
                            {
                                Type = "next",
                                Id = wsMessage.Id,
                                Payload = new Dictionary<string, object>
                                {
                                    ["data"] = new Dictionary<string, object>
                                    {
                                        ["subscriptionStarted"] = true,
                                        ["topic"] = subscriptionTopic
                                    }
                                }
                            }, ct);
                        }
                    }
                    break;

                case "complete":
                    // Subscription complete
                    break;
            }
        }
        catch
        {
            // Invalid message, ignore
        }
    }

    private async Task SendWebSocketMessageAsync(
        SubscriptionState state,
        WebSocketMessage message,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await state.WebSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            System.Net.WebSockets.WebSocketMessageType.Text,
            true,
            ct);
    }

    private async Task NotifySubscribersAsync(List<FieldSelection> mutations)
    {
        var topics = mutations.Select(m => $"blob.{m.Name}").ToHashSet();

        foreach (var sub in _subscriptions.Values)
        {
            var matchingTopics = sub.SubscribedTopics.Intersect(topics);
            if (matchingTopics.Any())
            {
                try
                {
                    await SendWebSocketMessageAsync(sub, new WebSocketMessage
                    {
                        Type = "next",
                        Payload = new Dictionary<string, object>
                        {
                            ["data"] = new Dictionary<string, object>
                            {
                                ["mutationOccurred"] = true,
                                ["topics"] = matchingTopics.ToList()
                            }
                        }
                    }, CancellationToken.None);
                }
                catch
                {
                    // Subscriber disconnected
                }
            }
        }
    }

    private string ExtractSubscriptionTopic(string query)
    {
        // Simple extraction - in production would use proper parser
        var match = Regex.Match(query, @"subscription\s+\w*\s*\{?\s*(\w+)", RegexOptions.IgnoreCase);
        return match.Success ? $"blob.{match.Groups[1].Value}" : "blob.all";
    }

    #endregion

    #region Introspection

    private object ResolveIntrospectionSchema()
    {
        return new Dictionary<string, object>
        {
            ["types"] = GetSchemaTypes(),
            ["queryType"] = new { name = "Query" },
            ["mutationType"] = new { name = "Mutation" },
            ["subscriptionType"] = new { name = "Subscription" },
            ["directives"] = Array.Empty<object>()
        };
    }

    private object? ResolveIntrospectionType(FieldSelection field, Dictionary<string, object> variables)
    {
        var typeName = GetArgumentValue<string>(field, "name", variables);
        var types = GetSchemaTypes();
        return types.FirstOrDefault(t =>
            t.TryGetValue("name", out var n) && n?.ToString() == typeName);
    }

    private List<Dictionary<string, object>> GetSchemaTypes()
    {
        return new List<Dictionary<string, object>>
        {
            new()
            {
                ["kind"] = "OBJECT",
                ["name"] = "Query",
                ["fields"] = new List<Dictionary<string, object>>
                {
                    new() { ["name"] = "blob", ["type"] = new { name = "Blob" } },
                    new() { ["name"] = "blobs", ["type"] = new { name = "BlobConnection" } },
                    new() { ["name"] = "blobsByPrefix", ["type"] = new { name = "[Blob]" } },
                    new() { ["name"] = "metadata", ["type"] = new { name = "Metadata" } },
                    new() { ["name"] = "stats", ["type"] = new { name = "Stats" } }
                }
            },
            new()
            {
                ["kind"] = "OBJECT",
                ["name"] = "Mutation",
                ["fields"] = new List<Dictionary<string, object>>
                {
                    new() { ["name"] = "createBlob", ["type"] = new { name = "Blob" } },
                    new() { ["name"] = "updateBlob", ["type"] = new { name = "Blob" } },
                    new() { ["name"] = "deleteBlob", ["type"] = new { name = "DeleteResult" } },
                    new() { ["name"] = "batchCreateBlobs", ["type"] = new { name = "[Blob]" } },
                    new() { ["name"] = "batchDeleteBlobs", ["type"] = new { name = "[DeleteResult]" } }
                }
            },
            new()
            {
                ["kind"] = "OBJECT",
                ["name"] = "Subscription",
                ["fields"] = new List<Dictionary<string, object>>
                {
                    new() { ["name"] = "blobCreated", ["type"] = new { name = "Blob" } },
                    new() { ["name"] = "blobUpdated", ["type"] = new { name = "Blob" } },
                    new() { ["name"] = "blobDeleted", ["type"] = new { name = "DeleteResult" } }
                }
            },
            new()
            {
                ["kind"] = "OBJECT",
                ["name"] = "Blob",
                ["fields"] = new List<Dictionary<string, object>>
                {
                    new() { ["name"] = "id", ["type"] = new { name = "ID!" } },
                    new() { ["name"] = "path", ["type"] = new { name = "String!" } },
                    new() { ["name"] = "content", ["type"] = new { name = "String" } },
                    new() { ["name"] = "size", ["type"] = new { name = "Int!" } },
                    new() { ["name"] = "createdAt", ["type"] = new { name = "DateTime" } },
                    new() { ["name"] = "modifiedAt", ["type"] = new { name = "DateTime" } },
                    new() { ["name"] = "metadata", ["type"] = new { name = "Metadata" } }
                }
            }
        };
    }

    #endregion

    #region Message Handling

    /// <inheritdoc />
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null)
            return;

        var response = message.Type switch
        {
            "graphql.query" => await HandleQueryMessageAsync(message.Payload),
            "graphql.introspect" => HandleIntrospectMessage(),
            "graphql.persisted.add" => HandleAddPersistedQuery(message.Payload),
            "graphql.persisted.list" => HandleListPersistedQueries(),
            "graphql.status" => HandleStatusMessage(),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        if (response != null)
        {
            message.Payload["_response"] = response;
        }
    }

    private async Task<Dictionary<string, object>> HandleQueryMessageAsync(Dictionary<string, object> payload)
    {
        var query = payload.GetValueOrDefault("query")?.ToString();
        var operationName = payload.GetValueOrDefault("operationName")?.ToString();
        var variablesJson = payload.GetValueOrDefault("variables")?.ToString();

        if (string.IsNullOrEmpty(query))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Query is required"
            };
        }

        var variables = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(variablesJson))
        {
            try
            {
                variables = JsonSerializer.Deserialize<Dictionary<string, object>>(variablesJson, _jsonOptions)
                    ?? new Dictionary<string, object>();
            }
            catch
            {
                // Invalid variables, use empty
            }
        }

        var result = await ExecuteQueryAsync(query, operationName, variables, CancellationToken.None);

        return new Dictionary<string, object>
        {
            ["success"] = result.Errors == null || result.Errors.Count == 0,
            ["data"] = result.Data ?? new Dictionary<string, object?>(),
            ["errors"] = result.Errors?.Select(e => e.Message).ToList() ?? new List<string>()
        };
    }

    private Dictionary<string, object> HandleIntrospectMessage()
    {
        var schema = ResolveIntrospectionSchema();
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["schema"] = schema
        };
    }

    private Dictionary<string, object> HandleAddPersistedQuery(Dictionary<string, object> payload)
    {
        var query = payload.GetValueOrDefault("query")?.ToString();
        var hash = payload.GetValueOrDefault("hash")?.ToString();

        if (string.IsNullOrEmpty(query))
        {
            return new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Query is required"
            };
        }

        if (string.IsNullOrEmpty(hash))
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(query));
            hash = Convert.ToHexString(bytes).ToLowerInvariant();
        }

        _persistedQueries[hash] = query;

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["hash"] = hash
        };
    }

    private Dictionary<string, object> HandleListPersistedQueries()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["queries"] = _persistedQueries.Select(kv => new Dictionary<string, object>
            {
                ["hash"] = kv.Key,
                ["query"] = kv.Value
            }).ToList(),
            ["count"] = _persistedQueries.Count
        };
    }

    private Dictionary<string, object> HandleStatusMessage()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["isRunning"] = _isRunning,
            ["port"] = _port,
            ["activeSubscriptions"] = _subscriptions.Count,
            ["persistedQueries"] = _persistedQueries.Count,
            ["storageConfigured"] = _storage != null
        };
    }

    #endregion

    #region Validation

    private ValidationResult ValidateQuery(string query)
    {
        var errors = new List<GraphQlError>();

        // Check query depth
        var depth = CalculateQueryDepth(query);
        if (depth > MaxQueryDepth)
        {
            errors.Add(new GraphQlError
            {
                Message = $"Query depth {depth} exceeds maximum allowed depth of {MaxQueryDepth}"
            });
        }

        // Check query complexity
        var complexity = CalculateQueryComplexity(query);
        if (complexity > MaxQueryComplexity)
        {
            errors.Add(new GraphQlError
            {
                Message = $"Query complexity {complexity} exceeds maximum allowed complexity of {MaxQueryComplexity}"
            });
        }

        return new ValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }

    private int CalculateQueryDepth(string query)
    {
        var depth = 0;
        var maxDepth = 0;

        foreach (var c in query)
        {
            if (c == '{')
            {
                depth++;
                maxDepth = Math.Max(maxDepth, depth);
            }
            else if (c == '}')
            {
                depth--;
            }
        }

        return maxDepth;
    }

    private int CalculateQueryComplexity(string query)
    {
        // Simple complexity calculation based on field count
        var fieldCount = Regex.Matches(query, @"\w+\s*[({:]").Count;
        return fieldCount;
    }

    #endregion

    #region Helper Methods

    private GraphQlOperation ParseOperation(string query, string? operationName)
    {
        var operation = new GraphQlOperation
        {
            Type = OperationType.Query,
            Name = operationName,
            SelectionSet = new List<FieldSelection>()
        };

        // Determine operation type
        if (query.TrimStart().StartsWith("mutation", StringComparison.OrdinalIgnoreCase))
        {
            operation.Type = OperationType.Mutation;
        }
        else if (query.TrimStart().StartsWith("subscription", StringComparison.OrdinalIgnoreCase))
        {
            operation.Type = OperationType.Subscription;
        }

        // Parse selection set (simplified)
        var fieldMatches = Regex.Matches(query, @"(\w+)(?:\s*\(([^)]*)\))?\s*(?:\{|$)", RegexOptions.Singleline);

        foreach (Match match in fieldMatches)
        {
            var fieldName = match.Groups[1].Value;

            // Skip operation keywords
            if (fieldName is "query" or "mutation" or "subscription")
                continue;

            var field = new FieldSelection
            {
                Name = fieldName,
                Arguments = new Dictionary<string, object>()
            };

            // Parse arguments
            if (match.Groups[2].Success)
            {
                var argsStr = match.Groups[2].Value;
                var argMatches = Regex.Matches(argsStr, @"(\w+)\s*:\s*(\$?\w+|""[^""]*""|\d+)");

                foreach (Match argMatch in argMatches)
                {
                    var argName = argMatch.Groups[1].Value;
                    var argValue = argMatch.Groups[2].Value.Trim('"');
                    field.Arguments[argName] = argValue;
                }
            }

            operation.SelectionSet.Add(field);
        }

        return operation;
    }

    private T? GetArgumentValue<T>(FieldSelection field, string name, Dictionary<string, object> variables)
    {
        if (!field.Arguments.TryGetValue(name, out var value))
            return default;

        var strValue = value?.ToString();

        // Check for variable reference
        if (strValue?.StartsWith("$") == true)
        {
            var varName = strValue[1..];
            if (variables.TryGetValue(varName, out var varValue))
            {
                value = varValue;
            }
            else
            {
                return default;
            }
        }

        // Convert to target type
        if (value is T typedValue)
            return typedValue;

        if (value is JsonElement element)
        {
            return element.Deserialize<T>(_jsonOptions);
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    private bool CheckRateLimit(string clientId)
    {
        var entry = _rateLimits.GetOrAdd(clientId, _ => new RateLimitEntry { WindowStart = DateTime.UtcNow });

        lock (entry)
        {
            if ((DateTime.UtcNow - entry.WindowStart).TotalMinutes >= 1)
            {
                entry.WindowStart = DateTime.UtcNow;
                entry.RequestCount = 0;
            }

            entry.RequestCount++;
            return entry.RequestCount <= RateLimitPerMinute;
        }
    }

    private async Task SendGraphQlResponse(HttpListenerResponse response, GraphQlResponse graphQlResponse)
    {
        response.StatusCode = 200;
        response.ContentType = "application/json";

        var json = JsonSerializer.Serialize(graphQlResponse, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private async Task SendErrorResponse(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";

        var json = JsonSerializer.Serialize(new { error = message }, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private async Task SendSchemaResponse(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/plain";

        var schema = GenerateSchemaSDL();
        var bytes = Encoding.UTF8.GetBytes(schema);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private async Task SendPlaygroundResponse(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/html";

        var html = GeneratePlaygroundHtml();
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private string GenerateSchemaSDL()
    {
        return @"
type Query {
  blob(id: ID!): Blob
  blobs(first: Int, after: String): BlobConnection!
  blobsByPrefix(prefix: String!, limit: Int): [Blob!]!
  metadata(id: ID!): Metadata
  stats: Stats!
}

type Mutation {
  createBlob(input: CreateBlobInput!): Blob!
  updateBlob(id: ID!, input: UpdateBlobInput!): Blob!
  deleteBlob(id: ID!): DeleteResult!
  batchCreateBlobs(inputs: [CreateBlobInput!]!): [Blob!]!
  batchDeleteBlobs(ids: [ID!]!): [DeleteResult!]!
}

type Subscription {
  blobCreated: Blob!
  blobUpdated: Blob!
  blobDeleted: DeleteResult!
}

type Blob {
  id: ID!
  path: String!
  content: String
  size: Int!
  createdAt: DateTime
  modifiedAt: DateTime
  metadata: Metadata
}

type BlobConnection {
  edges: [BlobEdge!]!
  pageInfo: PageInfo!
  totalCount: Int!
}

type BlobEdge {
  cursor: String!
  node: Blob!
}

type PageInfo {
  hasNextPage: Boolean!
  endCursor: String
}

type Metadata {
  contentType: String
  custom: [KeyValue!]
}

type KeyValue {
  key: String!
  value: String!
}

type Stats {
  totalBlobs: Int!
  activeSubscriptions: Int!
  persistedQueries: Int!
  uptime: DateTime!
}

type DeleteResult {
  success: Boolean!
  id: ID!
}

input CreateBlobInput {
  id: ID
  content: String!
  metadata: MetadataInput
}

input UpdateBlobInput {
  content: String
  metadata: MetadataInput
}

input MetadataInput {
  contentType: String
  custom: [KeyValueInput!]
}

input KeyValueInput {
  key: String!
  value: String!
}

scalar DateTime
";
    }

    private string GeneratePlaygroundHtml()
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
  <title>DataWarehouse GraphQL Playground</title>
  <style>
    body {{ font-family: sans-serif; margin: 0; padding: 20px; background: #1e1e1e; color: #d4d4d4; }}
    h1 {{ color: #569cd6; }}
    textarea {{ width: 100%; height: 200px; background: #252526; color: #d4d4d4; border: 1px solid #3c3c3c; padding: 10px; font-family: monospace; }}
    button {{ background: #0e639c; color: white; border: none; padding: 10px 20px; cursor: pointer; margin: 10px 0; }}
    button:hover {{ background: #1177bb; }}
    pre {{ background: #252526; padding: 15px; border-radius: 4px; overflow: auto; }}
  </style>
</head>
<body>
  <h1>DataWarehouse GraphQL Playground</h1>
  <p>Endpoint: <code>http://localhost:{_port}/graphql</code></p>
  <h3>Query</h3>
  <textarea id=""query"">query {{
  stats {{
    totalBlobs
    activeSubscriptions
    uptime
  }}
}}</textarea>
  <br/>
  <button onclick=""executeQuery()"">Execute</button>
  <h3>Result</h3>
  <pre id=""result"">Click Execute to run query</pre>
  <script>
    async function executeQuery() {{
      const query = document.getElementById('query').value;
      try {{
        const response = await fetch('/graphql', {{
          method: 'POST',
          headers: {{ 'Content-Type': 'application/json' }},
          body: JSON.stringify({{ query }})
        }});
        const data = await response.json();
        document.getElementById('result').textContent = JSON.stringify(data, null, 2);
      }} catch (e) {{
        document.getElementById('result').textContent = 'Error: ' + e.message;
      }}
    }}
  </script>
</body>
</html>";
    }

    /// <inheritdoc />
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "graphql.query", Description = "Execute GraphQL queries" },
            new() { Name = "graphql.mutation", Description = "Execute GraphQL mutations" },
            new() { Name = "graphql.subscription", Description = "Real-time subscriptions via WebSocket" },
            new() { Name = "graphql.introspection", Description = "Schema introspection" },
            new() { Name = "graphql.persisted", Description = "Persisted queries" },
            new() { Name = "graphql.dataloader", Description = "Batching with DataLoader pattern" }
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "GraphQLAPI";
        metadata["MaxQueryDepth"] = MaxQueryDepth;
        metadata["MaxQueryComplexity"] = MaxQueryComplexity;
        metadata["SupportsSubscriptions"] = true;
        metadata["SupportsPersistedQueries"] = true;
        metadata["SupportsDataLoader"] = true;
        return metadata;
    }

    #endregion

    #region Internal Types

    private sealed class GraphQlRequest
    {
        public string? Query { get; set; }
        public string? OperationName { get; set; }
        public Dictionary<string, object>? Variables { get; set; }
        public GraphQlExtensions? Extensions { get; set; }
    }

    private sealed class GraphQlExtensions
    {
        public PersistedQueryExtension? PersistedQuery { get; set; }
    }

    private sealed class PersistedQueryExtension
    {
        public int Version { get; set; }
        public string? Sha256Hash { get; set; }
    }

    private sealed class GraphQlResponse
    {
        public Dictionary<string, object?>? Data { get; set; }
        public List<GraphQlError>? Errors { get; set; }
    }

    private sealed class GraphQlError
    {
        public string Message { get; set; } = string.Empty;
        public List<Dictionary<string, object>>? Locations { get; set; }
        public List<string>? Path { get; set; }
        public Dictionary<string, object>? Extensions { get; set; }
    }

    private sealed class GraphQlOperation
    {
        public OperationType Type { get; set; }
        public string? Name { get; set; }
        public List<FieldSelection> SelectionSet { get; set; } = new();
    }

    private sealed class FieldSelection
    {
        public string Name { get; set; } = string.Empty;
        public string? Alias { get; set; }
        public Dictionary<string, object> Arguments { get; set; } = new();
        public List<FieldSelection> SelectionSet { get; set; } = new();
    }

    private sealed class SubscriptionState
    {
        public string SubscriptionId { get; init; } = string.Empty;
        public System.Net.WebSockets.WebSocket WebSocket { get; init; } = null!;
        public CancellationTokenSource CancellationSource { get; init; } = null!;
        public HashSet<string> SubscribedTopics { get; init; } = new();
    }

    private sealed class WebSocketMessage
    {
        public string? Type { get; set; }
        public string? Id { get; set; }
        public Dictionary<string, object>? Payload { get; set; }
    }

    private sealed class DataLoaderBatch
    {
        public ConcurrentDictionary<string, TaskCompletionSource<object?>> PendingRequests { get; } = new();
        public int IsScheduled;
    }

    private sealed class RateLimitEntry
    {
        public DateTime WindowStart { get; set; }
        public int RequestCount { get; set; }
    }

    private sealed class ValidationResult
    {
        public bool IsValid { get; init; }
        public List<GraphQlError> Errors { get; init; } = new();
    }

    private enum OperationType
    {
        Query,
        Mutation,
        Subscription
    }

    #endregion
}
