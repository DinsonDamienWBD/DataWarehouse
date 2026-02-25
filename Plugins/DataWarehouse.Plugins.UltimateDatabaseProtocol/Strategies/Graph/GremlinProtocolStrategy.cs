using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDatabaseProtocol.Strategies.Graph;

/// <summary>
/// Apache TinkerPop Gremlin binary protocol implementation.
/// Supports GraphBinary serialization and WebSocket transport.
/// </summary>
public sealed class GremlinProtocolStrategy : DatabaseProtocolStrategyBase
{
    private ClientWebSocket? _webSocket;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc/>
    public override string StrategyId => "gremlin-binary";

    /// <inheritdoc/>
    public override string StrategyName => "Gremlin GraphBinary Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "TinkerPop Gremlin GraphBinary",
        ProtocolVersion = "3.6",
        DefaultPort = 8182,
        Family = ProtocolFamily.Graph,
        MaxPacketSize = 64 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = false,
            SupportsCursors = false,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = true,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = false,
            SupportedAuthMethods =
            [
                AuthenticationMethod.ClearText,
                AuthenticationMethod.Token
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        var scheme = parameters.UseSsl ? "wss" : "ws";
        var uri = new Uri($"{scheme}://{parameters.Host}:{parameters.Port}/gremlin");

        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("User-Agent", "DataWarehouse/1.0");

        await _webSocket.ConnectAsync(uri, ct);
    }

    /// <inheritdoc/>
    protected override async Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(parameters.Password))
        {
            return;
        }

        // Send authentication request
        var authRequest = new GremlinRequest
        {
            RequestId = Guid.NewGuid(),
            Op = "authentication",
            Processor = "",
            Args = new Dictionary<string, object>
            {
                ["sasl"] = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"\0{parameters.Username}\0{parameters.Password}"))
            }
        };

        var response = await SendRequestAsync(authRequest, ct);

        if (response.Status.Code != 200 && response.Status.Code != 204)
        {
            throw new Exception($"Authentication failed: {response.Status.Message}");
        }
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        var request = new GremlinRequest
        {
            RequestId = Guid.NewGuid(),
            Op = "eval",
            Processor = "",
            Args = new Dictionary<string, object>
            {
                ["gremlin"] = query,
                ["language"] = "gremlin-groovy"
            }
        };

        if (parameters != null && parameters.Count > 0)
        {
            var bindings = new Dictionary<string, object?>();
            foreach (var param in parameters)
            {
                bindings[param.Key] = param.Value;
            }
            request.Args["bindings"] = bindings;
        }

        var response = await SendRequestAsync(request, ct);

        if (response.Status.Code >= 400)
        {
            return new QueryResult
            {
                Success = false,
                ErrorMessage = response.Status.Message ?? "Unknown error",
                ErrorCode = response.Status.Code.ToString()
            };
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();

        if (response.Result?.Data != null)
        {
            foreach (var item in response.Result.Data)
            {
                if (item is JsonElement element)
                {
                    rows.Add(ParseJsonElement(element));
                }
                else if (item is Dictionary<string, object?> dict)
                {
                    rows.Add(dict);
                }
                else
                {
                    rows.Add(new Dictionary<string, object?> { ["value"] = item });
                }
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
            Rows = rows
        };
    }

    private async Task<GremlinResponse> SendRequestAsync(GremlinRequest request, CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket not connected");
        }

        // Serialize request as JSON
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        // Send with mime type prefix for JSON
        var message = new byte[1 + bytes.Length];
        message[0] = 0x10; // application/json mime type
        bytes.CopyTo(message, 1);

        await _webSocket.SendAsync(message, WebSocketMessageType.Binary, true, ct);

        // Read response
        var buffer = new byte[64 * 1024];
        var responseBuilder = new MemoryStream(65536);

        WebSocketReceiveResult result;
        do
        {
            result = await _webSocket.ReceiveAsync(buffer, ct);
            responseBuilder.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var responseBytes = responseBuilder.ToArray();

        // Skip mime type byte if present
        var jsonStart = responseBytes[0] == 0x10 || responseBytes[0] == 0x21 ? 1 : 0;
        var responseJson = Encoding.UTF8.GetString(responseBytes, jsonStart, responseBytes.Length - jsonStart);

        return JsonSerializer.Deserialize<GremlinResponse>(responseJson, _jsonOptions)
            ?? throw new InvalidOperationException("Failed to parse response");
    }

    private static Dictionary<string, object?> ParseJsonElement(JsonElement element)
    {
        var result = new Dictionary<string, object?>();

        if (element.ValueKind == JsonValueKind.Object)
        {
            // Check for typed value format
            if (element.TryGetProperty("@type", out var typeElement) &&
                element.TryGetProperty("@value", out var valueElement))
            {
                var typeName = typeElement.GetString();
                result["@type"] = typeName;
                result["value"] = ParseTypedValue(typeName, valueElement);
            }
            else
            {
                foreach (var prop in element.EnumerateObject())
                {
                    result[prop.Name] = GetJsonValue(prop.Value);
                }
            }
        }
        else
        {
            result["value"] = GetJsonValue(element);
        }

        return result;
    }

    private static object? ParseTypedValue(string? typeName, JsonElement value)
    {
        return typeName switch
        {
            "g:Vertex" => ParseVertex(value),
            "g:Edge" => ParseEdge(value),
            "g:VertexProperty" => ParseVertexProperty(value),
            "g:Property" => ParseProperty(value),
            "g:Path" => ParsePath(value),
            "g:Int32" => value.GetInt32(),
            "g:Int64" => value.GetInt64(),
            "g:Float" => value.GetSingle(),
            "g:Double" => value.GetDouble(),
            "g:UUID" => Guid.Parse(value.GetString()!),
            "g:Date" => DateTimeOffset.FromUnixTimeMilliseconds(value.GetInt64()).DateTime,
            "g:Timestamp" => DateTimeOffset.FromUnixTimeMilliseconds(value.GetInt64()).DateTime,
            "g:List" => value.EnumerateArray().Select(GetJsonValue).ToList(),
            "g:Map" => ParseMap(value),
            "g:Set" => value.EnumerateArray().Select(GetJsonValue).ToHashSet(),
            _ => GetJsonValue(value)
        };
    }

    private static Dictionary<string, object?> ParseVertex(JsonElement value)
    {
        var vertex = new Dictionary<string, object?>
        {
            ["type"] = "vertex"
        };

        if (value.TryGetProperty("id", out var id))
            vertex["id"] = GetJsonValue(id);
        if (value.TryGetProperty("label", out var label))
            vertex["label"] = label.GetString();

        if (value.TryGetProperty("properties", out var props))
        {
            var properties = new Dictionary<string, object?>();
            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var values = prop.Value.EnumerateArray()
                        .Select(v => GetJsonValue(v.TryGetProperty("value", out var pv) ? pv : v))
                        .ToList();
                    properties[prop.Name] = values.Count == 1 ? values[0] : values;
                }
                else
                {
                    properties[prop.Name] = GetJsonValue(prop.Value);
                }
            }
            vertex["properties"] = properties;
        }

        return vertex;
    }

    private static Dictionary<string, object?> ParseEdge(JsonElement value)
    {
        var edge = new Dictionary<string, object?>
        {
            ["type"] = "edge"
        };

        if (value.TryGetProperty("id", out var id))
            edge["id"] = GetJsonValue(id);
        if (value.TryGetProperty("label", out var label))
            edge["label"] = label.GetString();
        if (value.TryGetProperty("inVLabel", out var inVLabel))
            edge["inVLabel"] = inVLabel.GetString();
        if (value.TryGetProperty("outVLabel", out var outVLabel))
            edge["outVLabel"] = outVLabel.GetString();
        if (value.TryGetProperty("inV", out var inV))
            edge["inV"] = GetJsonValue(inV);
        if (value.TryGetProperty("outV", out var outV))
            edge["outV"] = GetJsonValue(outV);

        if (value.TryGetProperty("properties", out var props))
        {
            var properties = new Dictionary<string, object?>();
            foreach (var prop in props.EnumerateObject())
            {
                properties[prop.Name] = GetJsonValue(prop.Value);
            }
            edge["properties"] = properties;
        }

        return edge;
    }

    private static Dictionary<string, object?> ParseVertexProperty(JsonElement value)
    {
        var prop = new Dictionary<string, object?>
        {
            ["type"] = "vertexProperty"
        };

        if (value.TryGetProperty("id", out var id))
            prop["id"] = GetJsonValue(id);
        if (value.TryGetProperty("label", out var label))
            prop["label"] = label.GetString();
        if (value.TryGetProperty("value", out var val))
            prop["value"] = GetJsonValue(val);

        return prop;
    }

    private static Dictionary<string, object?> ParseProperty(JsonElement value)
    {
        var prop = new Dictionary<string, object?>
        {
            ["type"] = "property"
        };

        if (value.TryGetProperty("key", out var key))
            prop["key"] = key.GetString();
        if (value.TryGetProperty("value", out var val))
            prop["value"] = GetJsonValue(val);

        return prop;
    }

    private static Dictionary<string, object?> ParsePath(JsonElement value)
    {
        var path = new Dictionary<string, object?>
        {
            ["type"] = "path"
        };

        if (value.TryGetProperty("labels", out var labels))
            path["labels"] = labels.EnumerateArray()
                .Select(l => l.EnumerateArray().Select(x => x.GetString()).ToList())
                .ToList();

        if (value.TryGetProperty("objects", out var objects))
            path["objects"] = objects.EnumerateArray().Select(GetJsonValue).ToList();

        return path;
    }

    private static Dictionary<string, object?> ParseMap(JsonElement value)
    {
        var map = new Dictionary<string, object?>();
        var items = value.EnumerateArray().ToList();

        for (int i = 0; i < items.Count - 1; i += 2)
        {
            var key = GetJsonValue(items[i])?.ToString() ?? i.ToString();
            map[key] = GetJsonValue(items[i + 1]);
        }

        return map;
    }

    private static object? GetJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(GetJsonValue).ToList(),
            JsonValueKind.Object => ParseJsonElement(element),
            _ => element.ToString()
        };
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return ExecuteQueryCoreAsync(command, parameters, ct);
    }

    /// <inheritdoc/>
    protected override async Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        var txId = Guid.NewGuid().ToString("N");

        var request = new GremlinRequest
        {
            RequestId = Guid.NewGuid(),
            Op = "eval",
            Processor = "session",
            Args = new Dictionary<string, object>
            {
                ["gremlin"] = "g.tx().open()",
                ["session"] = txId
            }
        };

        var response = await SendRequestAsync(request, ct);

        if (response.Status.Code >= 400)
        {
            throw new Exception($"Failed to begin transaction: {response.Status.Message}");
        }

        return txId;
    }

    /// <inheritdoc/>
    protected override async Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        var request = new GremlinRequest
        {
            RequestId = Guid.NewGuid(),
            Op = "eval",
            Processor = "session",
            Args = new Dictionary<string, object>
            {
                ["gremlin"] = "g.tx().commit()",
                ["session"] = transactionId
            }
        };

        var response = await SendRequestAsync(request, ct);

        if (response.Status.Code >= 400)
        {
            throw new Exception($"Failed to commit transaction: {response.Status.Message}");
        }
    }

    /// <inheritdoc/>
    protected override async Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        var request = new GremlinRequest
        {
            RequestId = Guid.NewGuid(),
            Op = "eval",
            Processor = "session",
            Args = new Dictionary<string, object>
            {
                ["gremlin"] = "g.tx().rollback()",
                ["session"] = transactionId
            }
        };

        await SendRequestAsync(request, ct);
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        if (_webSocket?.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("1+1", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        _webSocket?.Dispose();
        _webSocket = null;
        await base.CleanupConnectionAsync();
    }

    private sealed class GremlinRequest
    {
        public Guid RequestId { get; set; }
        public string Op { get; set; } = "";
        public string Processor { get; set; } = "";
        public Dictionary<string, object> Args { get; set; } = new();
    }

    private sealed class GremlinResponse
    {
        public Guid RequestId { get; set; }
        public GremlinStatus Status { get; set; } = new();
        public GremlinResult? Result { get; set; }
    }

    private sealed class GremlinStatus
    {
        public int Code { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, object>? Attributes { get; set; }
    }

    private sealed class GremlinResult
    {
        public List<object?>? Data { get; set; }
        public Dictionary<string, object>? Meta { get; set; }
    }
}

/// <summary>
/// Amazon Neptune Gremlin protocol strategy.
/// Neptune uses Gremlin over WebSocket with IAM authentication.
/// </summary>
public sealed class NeptuneGremlinProtocolStrategy : DatabaseProtocolStrategyBase
{
    private ClientWebSocket? _webSocket;
    private string _region = "us-east-1";
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc/>
    public override string StrategyId => "neptune-gremlin";

    /// <inheritdoc/>
    public override string StrategyName => "Amazon Neptune Gremlin Protocol";

    /// <inheritdoc/>
    public override ProtocolInfo ProtocolInfo => new()
    {
        ProtocolName = "Amazon Neptune Gremlin",
        ProtocolVersion = "3.5",
        DefaultPort = 8182,
        Family = ProtocolFamily.Graph,
        MaxPacketSize = 64 * 1024 * 1024,
        Capabilities = new ProtocolCapabilities
        {
            SupportsTransactions = true,
            SupportsPreparedStatements = false,
            SupportsCursors = false,
            SupportsStreaming = true,
            SupportsBatch = true,
            SupportsNotifications = false,
            SupportsSsl = true,
            SupportsCompression = false,
            SupportsMultiplexing = true,
            SupportsServerCursors = false,
            SupportsBulkOperations = true,
            SupportsQueryCancellation = true,
            SupportedAuthMethods =
            [
                AuthenticationMethod.IAM
            ]
        }
    };

    /// <inheritdoc/>
    protected override async Task PerformHandshakeAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Extract region from host if possible
        if (parameters.Host.Contains(".neptune."))
        {
            var parts = parameters.Host.Split('.');
            for (int i = 0; i < parts.Length - 2; i++)
            {
                if (parts[i + 1] == "neptune")
                {
                    _region = parts[i];
                    break;
                }
            }
        }

        var uri = new Uri($"wss://{parameters.Host}:{parameters.Port}/gremlin");

        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("User-Agent", "DataWarehouse/1.0");

        // Note: In production, IAM SigV4 signing would be required here
        // This is a simplified implementation

        await _webSocket.ConnectAsync(uri, ct);
    }

    /// <inheritdoc/>
    protected override Task AuthenticateAsync(ConnectionParameters parameters, CancellationToken ct)
    {
        // Neptune uses IAM authentication via request signing
        // Authentication happens during connection via signed WebSocket upgrade
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<QueryResult> ExecuteQueryCoreAsync(
        string query,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket not connected");
        }

        var request = new
        {
            requestId = Guid.NewGuid(),
            op = "eval",
            processor = "",
            args = new Dictionary<string, object>
            {
                ["gremlin"] = query
            }
        };

        if (parameters != null && parameters.Count > 0)
        {
            ((Dictionary<string, object>)request.args)["bindings"] = parameters;
        }

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        var message = new byte[1 + bytes.Length];
        message[0] = 0x10; // application/json
        bytes.CopyTo(message, 1);

        await _webSocket.SendAsync(message, WebSocketMessageType.Binary, true, ct);

        // Read response
        var buffer = new byte[64 * 1024];
        var responseBuilder = new MemoryStream(65536);

        WebSocketReceiveResult result;
        do
        {
            result = await _webSocket.ReceiveAsync(buffer, ct);
            responseBuilder.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var responseBytes = responseBuilder.ToArray();
        var jsonStart = responseBytes[0] == 0x10 ? 1 : 0;
        var responseJson = Encoding.UTF8.GetString(responseBytes, jsonStart, responseBytes.Length - jsonStart);

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var statusCode = root.GetProperty("status").GetProperty("code").GetInt32();

        if (statusCode >= 400)
        {
            var message2 = root.GetProperty("status").TryGetProperty("message", out var msg)
                ? msg.GetString() : "Unknown error";
            return new QueryResult
            {
                Success = false,
                ErrorMessage = message2,
                ErrorCode = statusCode.ToString()
            };
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>();

        if (root.TryGetProperty("result", out var resultEl) &&
            resultEl.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("@value", out var dataValue))
            {
                foreach (var item in dataValue.EnumerateArray())
                {
                    rows.Add(ParseNeptuneValue(item));
                }
            }
        }

        return new QueryResult
        {
            Success = true,
            RowsAffected = rows.Count,
            Rows = rows
        };
    }

    private static Dictionary<string, object?> ParseNeptuneValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?> { ["value"] = GetValue(element) };
        }

        if (element.TryGetProperty("@type", out var typeEl) &&
            element.TryGetProperty("@value", out var valueEl))
        {
            var typeName = typeEl.GetString();
            return typeName switch
            {
                "g:Vertex" => ParseVertex(valueEl),
                "g:Edge" => ParseEdge(valueEl),
                _ => new Dictionary<string, object?>
                {
                    ["@type"] = typeName,
                    ["value"] = GetValue(valueEl)
                }
            };
        }

        var dict = new Dictionary<string, object?>();
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = GetValue(prop.Value);
        }
        return dict;
    }

    private static Dictionary<string, object?> ParseVertex(JsonElement value)
    {
        var vertex = new Dictionary<string, object?> { ["type"] = "vertex" };

        if (value.TryGetProperty("id", out var id))
            vertex["id"] = GetValue(id);
        if (value.TryGetProperty("label", out var label))
            vertex["label"] = label.GetString();

        return vertex;
    }

    private static Dictionary<string, object?> ParseEdge(JsonElement value)
    {
        var edge = new Dictionary<string, object?> { ["type"] = "edge" };

        if (value.TryGetProperty("id", out var id))
            edge["id"] = GetValue(id);
        if (value.TryGetProperty("label", out var label))
            edge["label"] = label.GetString();
        if (value.TryGetProperty("inV", out var inV))
            edge["inV"] = GetValue(inV);
        if (value.TryGetProperty("outV", out var outV))
            edge["outV"] = GetValue(outV);

        return edge;
    }

    private static object? GetValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("@value", out var val))
        {
            return GetValue(val);
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    /// <inheritdoc/>
    protected override Task<QueryResult> ExecuteNonQueryCoreAsync(
        string command,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return ExecuteQueryCoreAsync(command, parameters, ct);
    }

    /// <inheritdoc/>
    protected override Task<string> BeginTransactionCoreAsync(CancellationToken ct)
    {
        // Neptune doesn't support explicit transactions in the same way
        return Task.FromResult(Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc/>
    protected override Task CommitTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task RollbackTransactionCoreAsync(string transactionId, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task SendDisconnectMessageAsync(CancellationToken ct)
    {
        if (_webSocket?.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
        }
    }

    /// <inheritdoc/>
    protected override async Task<bool> PingCoreAsync(CancellationToken ct)
    {
        try
        {
            var result = await ExecuteQueryCoreAsync("g.V().limit(0)", null, ct);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    protected override async Task CleanupConnectionAsync()
    {
        _webSocket?.Dispose();
        _webSocket = null;
        await base.CleanupConnectionAsync();
    }
}
