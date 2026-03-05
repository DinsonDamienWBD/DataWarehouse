using System.Text;

namespace DataWarehouse.Plugins.UltimateSDKPorts.Strategies.GoBindings;

/// <summary>Go CGO binding strategy.</summary>
public sealed class GoCgoStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GoCgo",
        Description = "Go bindings using CGO for direct C library integration",
        Category = SDKPortCategory.GoBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Go],
            SupportedTransports: [TransportType.FFI],
            SupportsAsync: false, SupportsStreaming: false, SupportsCallbacks: true,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["go", "cgo", "ffi"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Go CGO bindings");
        sb.AppendLine("package datawarehouse\n");
        sb.AppendLine("/*");
        sb.AppendLine("#cgo LDFLAGS: -ldatawarehouse");
        sb.AppendLine("#include <datawarehouse.h>");
        sb.AppendLine("*/");
        sb.AppendLine("import \"C\"\n");

        foreach (var method in _registeredMethods.Values)
        {
            var methodParams = string.Join(", ", method.ParameterTypes.Select((t, i) => $"arg{i} {MapType(t, LanguageTarget.Go)}"));
            sb.AppendLine($"// {method.Description}");
            sb.AppendLine($"func {method.MethodName}({methodParams}) {MapType(method.ReturnType, LanguageTarget.Go)} {{");
            sb.AppendLine($"    return {MapType(method.ReturnType, LanguageTarget.Go)}(C.{method.MethodName}())");
            sb.AppendLine("}");
        }
        return Task.FromResult(sb.ToString());
    }
}

/// <summary>Go gRPC binding strategy.</summary>
public sealed class GoGrpcStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GoGrpc",
        Description = "Go bindings using gRPC for high-performance RPC with protobuf",
        Category = SDKPortCategory.GoBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Go],
            SupportedTransports: [TransportType.GRPC],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: false,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 10000),
        Tags = ["go", "grpc", "protobuf"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Go gRPC client");
        sb.AppendLine("package datawarehouse\n");
        sb.AppendLine("import (");
        sb.AppendLine("    \"context\"");
        sb.AppendLine("    \"google.golang.org/grpc\"");
        sb.AppendLine("    \"google.golang.org/grpc/credentials\"");
        sb.AppendLine(")\n");
        sb.AppendLine("type Client struct {");
        sb.AppendLine("    conn   *grpc.ClientConn");
        sb.AppendLine("    client DataWarehouseServiceClient");
        sb.AppendLine("}\n");
        // Cat 15 (finding 3800): use grpc.WithTransportCredentials(credentials.NewTLS(...)) for TLS-enabled connections.
        // grpc.WithInsecure() ships TLS-disabled gRPC to consumers — unsafe for production.
        sb.AppendLine("func NewClient(addr string) (*Client, error) {");
        sb.AppendLine("    // Use TLS by default. For insecure connections (e.g. localhost loopback),");
        sb.AppendLine("    // replace credentials.NewTLS(nil) with insecure.NewCredentials() from google.golang.org/grpc/credentials/insecure.");
        sb.AppendLine("    creds := credentials.NewTLS(nil) // system CA pool");
        sb.AppendLine("    conn, err := grpc.Dial(addr, grpc.WithTransportCredentials(creds))");
        sb.AppendLine("    if err != nil { return nil, err }");
        sb.AppendLine("    return &Client{conn: conn, client: NewDataWarehouseServiceClient(conn)}, nil");
        sb.AppendLine("}\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"func (c *Client) {method.MethodName}(ctx context.Context, req *{method.MethodName}Request) (*{method.MethodName}Response, error) {{");
            sb.AppendLine($"    return c.client.{method.MethodName}(ctx, req)");
            sb.AppendLine("}");
        }
        return Task.FromResult(sb.ToString());
    }
}

/// <summary>Go HTTP client binding strategy.</summary>
public sealed class GoHttpClientStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GoHttpClient",
        Description = "Go bindings using net/http for REST API communication",
        Category = SDKPortCategory.GoBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Go],
            SupportedTransports: [TransportType.REST],
            SupportsAsync: true, SupportsStreaming: false, SupportsCallbacks: false,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["go", "http", "rest"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Go HTTP client");
        sb.AppendLine("package datawarehouse\n");
        sb.AppendLine("import (");
        sb.AppendLine("    \"bytes\"");
        sb.AppendLine("    \"encoding/json\"");
        sb.AppendLine("    \"net/http\"");
        sb.AppendLine(")\n");
        sb.AppendLine("type Client struct { baseURL string; httpClient *http.Client }\n");
        sb.AppendLine("func NewClient(baseURL string) *Client {");
        sb.AppendLine("    return &Client{baseURL: baseURL, httpClient: &http.Client{}}");
        sb.AppendLine("}\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"func (c *Client) {method.MethodName}(params map[string]interface{{}}) (map[string]interface{{}}, error) {{");
            sb.AppendLine("    body, _ := json.Marshal(params)");
            sb.AppendLine($"    resp, err := c.httpClient.Post(c.baseURL+\"/api/{method.MethodName}\", \"application/json\", bytes.NewReader(body))");
            sb.AppendLine("    if err != nil { return nil, err }");
            sb.AppendLine("    defer resp.Body.Close()");
            sb.AppendLine("    var result map[string]interface{}");
            sb.AppendLine("    json.NewDecoder(resp.Body).Decode(&result)");
            sb.AppendLine("    return result, nil");
            sb.AppendLine("}");
        }
        return Task.FromResult(sb.ToString());
    }
}

/// <summary>Go channel-based async binding strategy.</summary>
public sealed class GoChannelStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "GoChannel",
        Description = "Go bindings using channels for async message passing",
        Category = SDKPortCategory.GoBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Go],
            SupportedTransports: [TransportType.MessageQueue],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: true,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["go", "channel", "async"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Go channel-based client");
        sb.AppendLine("package datawarehouse\n");
        sb.AppendLine("type Request struct { Method string; Params map[string]interface{}; Response chan Response }");
        sb.AppendLine("type Response struct { Result interface{}; Error error }\n");
        sb.AppendLine("type AsyncClient struct { requests chan Request }\n");
        sb.AppendLine("func NewAsyncClient() *AsyncClient {");
        sb.AppendLine("    c := &AsyncClient{requests: make(chan Request, 100)}");
        sb.AppendLine("    go c.processRequests()");
        sb.AppendLine("    return c");
        sb.AppendLine("}\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"func (c *AsyncClient) {method.MethodName}Async(params map[string]interface{{}}) <-chan Response {{");
            sb.AppendLine($"    resp := make(chan Response, 1)");
            sb.AppendLine($"    c.requests <- Request{{Method: \"{method.MethodName}\", Params: params, Response: resp}}");
            sb.AppendLine("    return resp");
            sb.AppendLine("}");
        }
        return Task.FromResult(sb.ToString());
    }
}
