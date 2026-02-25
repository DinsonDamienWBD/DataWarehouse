using System.Text;

namespace DataWarehouse.Plugins.UltimateSDKPorts.Strategies.JavaScriptBindings;

/// <summary>JavaScript Node.js native addon binding strategy.</summary>
public sealed class NodeNativeAddonStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "NodeNativeAddon",
        Description = "Node.js native addon bindings using N-API for high-performance native integration",
        Category = SDKPortCategory.JavaScriptBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.JavaScript, LanguageTarget.TypeScript],
            SupportedTransports: [TransportType.FFI],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: true,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 5000),
        Tags = ["javascript", "nodejs", "napi", "native"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        if (language == LanguageTarget.TypeScript)
        {
            sb.AppendLine("// Auto-generated TypeScript definitions for native addon");
            sb.AppendLine("declare module 'datawarehouse-native' {");
            foreach (var method in _registeredMethods.Values)
            {
                var methodParams = string.Join(", ", method.ParameterTypes.Select((t, i) => $"arg{i}: {MapType(t, LanguageTarget.TypeScript)}"));
                sb.AppendLine($"  export function {ToCamelCase(method.MethodName)}({methodParams}): Promise<{MapType(method.ReturnType, LanguageTarget.TypeScript)}>;");
            }
            sb.AppendLine("}");
        }
        else
        {
            sb.AppendLine("// Auto-generated Node.js native addon wrapper");
            sb.AppendLine("const native = require('./build/Release/datawarehouse.node');\n");
            sb.AppendLine("module.exports = {");
            foreach (var method in _registeredMethods.Values)
                sb.AppendLine($"  {ToCamelCase(method.MethodName)}: native.{method.MethodName},");
            sb.AppendLine("};");
        }
        return Task.FromResult(sb.ToString());
    }

    private static string ToCamelCase(string name) => char.ToLower(name[0]) + name[1..];
}

/// <summary>JavaScript WebSocket binding strategy.</summary>
public sealed class JavaScriptWebSocketStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "JavaScriptWebSocket",
        Description = "JavaScript bindings using WebSocket for real-time bidirectional communication",
        Category = SDKPortCategory.JavaScriptBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.JavaScript, LanguageTarget.TypeScript],
            SupportedTransports: [TransportType.WebSocket],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: true,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["javascript", "websocket", "realtime"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine(language == LanguageTarget.TypeScript
            ? "// Auto-generated TypeScript WebSocket client"
            : "// Auto-generated JavaScript WebSocket client");
        sb.AppendLine("export class DataWarehouseClient {");
        sb.AppendLine("  private ws: WebSocket;");
        sb.AppendLine("  private pending: Map<string, { resolve: Function, reject: Function }> = new Map();\n");
        sb.AppendLine("  constructor(url: string = 'ws://localhost:8080') {");
        sb.AppendLine("    this.ws = new WebSocket(url);");
        sb.AppendLine("    this.ws.onmessage = (e) => this.handleMessage(JSON.parse(e.data));");
        sb.AppendLine("  }\n");

        foreach (var method in _registeredMethods.Values)
        {
            var returnType = language == LanguageTarget.TypeScript ? $": Promise<{MapType(method.ReturnType, LanguageTarget.TypeScript)}>" : "";
            sb.AppendLine($"  async {ToCamelCase(method.MethodName)}(params: any){returnType} {{");
            sb.AppendLine($"    return this.call('{method.MethodName}', params);");
            sb.AppendLine("  }");
        }
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToCamelCase(string name) => char.ToLower(name[0]) + name[1..];
}

/// <summary>JavaScript gRPC-Web binding strategy.</summary>
public sealed class JavaScriptGrpcWebStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "JavaScriptGrpcWeb",
        Description = "JavaScript bindings using gRPC-Web for browser-compatible gRPC",
        Category = SDKPortCategory.JavaScriptBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.JavaScript, LanguageTarget.TypeScript],
            SupportedTransports: [TransportType.GRPC],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: false,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 10000),
        Tags = ["javascript", "grpc", "grpc-web"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated gRPC-Web client");
        sb.AppendLine("import { grpc } from '@improbable-eng/grpc-web';");
        sb.AppendLine("import { DataWarehouseService } from './datawarehouse_pb_service';\n");
        sb.AppendLine("export class DataWarehouseClient {");
        sb.AppendLine("  constructor(private host: string = 'http://localhost:8080') {}\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"  {ToCamelCase(method.MethodName)}(request: any): Promise<any> {{");
            sb.AppendLine($"    return new Promise((resolve, reject) => {{");
            sb.AppendLine($"      grpc.unary(DataWarehouseService.{method.MethodName}, {{");
            sb.AppendLine("        request, host: this.host,");
            sb.AppendLine("        onEnd: (res) => res.status === grpc.Code.OK ? resolve(res.message) : reject(res.statusMessage)");
            sb.AppendLine("      });");
            sb.AppendLine("    });");
            sb.AppendLine("  }");
        }
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToCamelCase(string name) => char.ToLower(name[0]) + name[1..];
}

/// <summary>JavaScript REST/fetch binding strategy.</summary>
public sealed class JavaScriptFetchStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "JavaScriptFetch",
        Description = "JavaScript bindings using fetch API for REST communication",
        Category = SDKPortCategory.JavaScriptBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.JavaScript, LanguageTarget.TypeScript],
            SupportedTransports: [TransportType.REST],
            SupportsAsync: true, SupportsStreaming: false, SupportsCallbacks: false,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["javascript", "fetch", "rest"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated fetch-based REST client");
        sb.AppendLine("export class DataWarehouseClient {");
        sb.AppendLine("  constructor(private baseUrl: string = 'http://localhost:8080') {}\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"  async {ToCamelCase(method.MethodName)}(params: any): Promise<any> {{");
            sb.AppendLine($"    const res = await fetch(`${{this.baseUrl}}/api/{method.MethodName}`, {{");
            sb.AppendLine("      method: 'POST', headers: { 'Content-Type': 'application/json' },");
            sb.AppendLine("      body: JSON.stringify(params)");
            sb.AppendLine("    });");
            sb.AppendLine("    return res.json();");
            sb.AppendLine("  }");
        }
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToCamelCase(string name) => char.ToLower(name[0]) + name[1..];
}
