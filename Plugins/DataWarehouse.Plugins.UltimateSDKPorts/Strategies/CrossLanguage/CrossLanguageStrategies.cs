using System.Text;

namespace DataWarehouse.Plugins.UltimateSDKPorts.Strategies.CrossLanguage;

/// <summary>Universal gRPC binding strategy for all languages.</summary>
public sealed class UniversalGrpcStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "UniversalGrpc",
        Description = "Universal gRPC bindings supporting all major languages through protobuf",
        Category = SDKPortCategory.CrossLanguage,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python, LanguageTarget.JavaScript, LanguageTarget.TypeScript,
                                LanguageTarget.Go, LanguageTarget.Rust, LanguageTarget.Java, LanguageTarget.CSharp],
            SupportedTransports: [TransportType.GRPC],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: false,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 100000),
        Tags = ["universal", "grpc", "protobuf", "cross-language"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Protocol Buffer definitions");
        sb.AppendLine("syntax = \"proto3\";\n");
        sb.AppendLine("package datawarehouse;\n");
        sb.AppendLine("service DataWarehouse {");
        foreach (var method in _registeredMethods.Values)
        {
            var streamPrefix = method.IsStreaming ? "stream " : "";
            sb.AppendLine($"  rpc {method.MethodName}({method.MethodName}Request) returns ({streamPrefix}{method.MethodName}Response);");
        }
        sb.AppendLine("}\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"message {method.MethodName}Request {{");
            for (var i = 0; i < method.ParameterTypes.Length; i++)
                sb.AppendLine($"  {MapProtoType(method.ParameterTypes[i])} arg{i} = {i + 1};");
            sb.AppendLine("}\n");
            sb.AppendLine($"message {method.MethodName}Response {{");
            sb.AppendLine($"  {MapProtoType(method.ReturnType)} result = 1;");
            sb.AppendLine("}");
        }
        return Task.FromResult(sb.ToString());
    }

    private static string MapProtoType(string type) => type.ToLower() switch
    {
        "string" => "string",
        "int" or "int32" => "int32",
        "long" or "int64" => "int64",
        "bool" or "boolean" => "bool",
        "double" => "double",
        "float" => "float",
        "bytes" or "byte[]" => "bytes",
        _ => "bytes"
    };
}

/// <summary>OpenAPI/REST binding strategy for all languages.</summary>
public sealed class OpenApiStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "OpenApi",
        Description = "OpenAPI 3.0 bindings with auto-generated clients for all languages",
        Category = SDKPortCategory.CrossLanguage,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python, LanguageTarget.JavaScript, LanguageTarget.TypeScript,
                                LanguageTarget.Go, LanguageTarget.Rust, LanguageTarget.Java, LanguageTarget.Ruby,
                                LanguageTarget.CSharp, LanguageTarget.Swift, LanguageTarget.Kotlin],
            SupportedTransports: [TransportType.REST],
            SupportsAsync: true, SupportsStreaming: false, SupportsCallbacks: false,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["openapi", "rest", "swagger", "cross-language"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("openapi: 3.0.3");
        sb.AppendLine("info:");
        sb.AppendLine("  title: DataWarehouse API");
        sb.AppendLine("  version: 1.0.0");
        sb.AppendLine("paths:");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"  /api/{method.MethodName}:");
            sb.AppendLine("    post:");
            sb.AppendLine($"      summary: {method.Description}");
            sb.AppendLine($"      operationId: {ToCamelCase(method.MethodName)}");
            sb.AppendLine("      requestBody:");
            sb.AppendLine("        content:");
            sb.AppendLine("          application/json:");
            sb.AppendLine("            schema:");
            sb.AppendLine($"              $ref: '#/components/schemas/{method.MethodName}Request'");
            sb.AppendLine("      responses:");
            sb.AppendLine("        '200':");
            sb.AppendLine("          description: Success");
        }
        return Task.FromResult(sb.ToString());
    }

    private static string ToCamelCase(string name) => char.ToLower(name[0]) + name[1..];
}

/// <summary>JSON-RPC binding strategy for all languages.</summary>
public sealed class JsonRpcStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "JsonRpc",
        Description = "JSON-RPC 2.0 bindings for language-agnostic RPC",
        Category = SDKPortCategory.CrossLanguage,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python, LanguageTarget.JavaScript, LanguageTarget.TypeScript,
                                LanguageTarget.Go, LanguageTarget.Rust, LanguageTarget.Java, LanguageTarget.Ruby],
            SupportedTransports: [TransportType.REST, TransportType.WebSocket],
            SupportsAsync: true, SupportsStreaming: false, SupportsCallbacks: true,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["jsonrpc", "rpc", "cross-language"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// JSON-RPC 2.0 method definitions");
        sb.AppendLine("{");
        sb.AppendLine("  \"methods\": [");
        var methods = _registeredMethods.Values.ToList();
        for (var i = 0; i < methods.Count; i++)
        {
            var method = methods[i];
            sb.AppendLine("    {");
            sb.AppendLine($"      \"name\": \"{method.MethodName}\",");
            sb.AppendLine($"      \"description\": \"{method.Description}\",");
            sb.AppendLine($"      \"params\": [{string.Join(", ", method.ParameterTypes.Select((t, j) => $"{{\"name\": \"arg{j}\", \"type\": \"{t}\"}}"))}],");
            sb.AppendLine($"      \"result\": {{\"type\": \"{method.ReturnType}\"}}");
            sb.AppendLine("    }" + (i < methods.Count - 1 ? "," : ""));
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }
}

/// <summary>MessagePack binary serialization binding strategy.</summary>
public sealed class MessagePackStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "MessagePack",
        Description = "MessagePack binary serialization for efficient cross-language communication",
        Category = SDKPortCategory.CrossLanguage,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python, LanguageTarget.JavaScript, LanguageTarget.Go,
                                LanguageTarget.Rust, LanguageTarget.Java, LanguageTarget.CSharp],
            SupportedTransports: [TransportType.MessageQueue, TransportType.WebSocket],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: true,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 50000),
        Tags = ["messagepack", "binary", "cross-language"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default) =>
        Task.FromResult($"// MessagePack schema for {language}\n// Schema generation for efficient binary serialization");
}

/// <summary>Apache Thrift binding strategy.</summary>
public sealed class ThriftStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "Thrift",
        Description = "Apache Thrift bindings for cross-language service definitions",
        Category = SDKPortCategory.CrossLanguage,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python, LanguageTarget.JavaScript, LanguageTarget.Go,
                                LanguageTarget.Rust, LanguageTarget.Java, LanguageTarget.CSharp, LanguageTarget.Cpp],
            SupportedTransports: [TransportType.GRPC, TransportType.REST],
            SupportsAsync: true, SupportsStreaming: false, SupportsCallbacks: false,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["thrift", "apache", "cross-language"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Thrift IDL");
        sb.AppendLine("namespace * datawarehouse\n");
        sb.AppendLine("service DataWarehouse {");
        foreach (var method in _registeredMethods.Values)
        {
            var thriftParams = string.Join(", ", method.ParameterTypes.Select((t, i) => $"{i + 1}: {MapThriftType(t)} arg{i}"));
            sb.AppendLine($"  {MapThriftType(method.ReturnType)} {method.MethodName}({thriftParams})");
        }
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string MapThriftType(string type) => type.ToLower() switch
    {
        "string" => "string",
        "int" or "int32" => "i32",
        "long" or "int64" => "i64",
        "bool" or "boolean" => "bool",
        "double" => "double",
        "bytes" or "byte[]" => "binary",
        _ => "binary"
    };
}

/// <summary>Cap'n Proto binding strategy.</summary>
public sealed class CapnProtoStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "CapnProto",
        Description = "Cap'n Proto bindings for zero-copy serialization",
        Category = SDKPortCategory.CrossLanguage,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Rust, LanguageTarget.Go, LanguageTarget.Cpp, LanguageTarget.Python],
            SupportedTransports: [TransportType.FFI, TransportType.MessageQueue],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: false,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 100000),
        Tags = ["capnproto", "zero-copy", "cross-language"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Auto-generated Cap'n Proto schema");
        sb.AppendLine("@0xdbb9ad1f14bf0b36;\n");
        sb.AppendLine("interface DataWarehouse {");
        foreach (var method in _registeredMethods.Values)
            sb.AppendLine($"  {ToCamelCase(method.MethodName)} @0 (request :{method.MethodName}Request) -> (response :{method.MethodName}Response);");
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToCamelCase(string name) => char.ToLower(name[0]) + name[1..];
}
