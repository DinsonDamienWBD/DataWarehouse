using System.Text;

namespace DataWarehouse.Plugins.UltimateSDKPorts.Strategies.PythonBindings;

/// <summary>Python ctypes FFI binding strategy.</summary>
public sealed class PythonCtypesStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "PythonCtypes",
        Description = "Python bindings using ctypes for direct C library calls",
        Category = SDKPortCategory.PythonBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python],
            SupportedTransports: [TransportType.FFI, TransportType.SharedMemory],
            SupportsAsync: false, SupportsStreaming: false, SupportsCallbacks: true,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["python", "ctypes", "ffi"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Auto-generated Python ctypes bindings");
        sb.AppendLine("import ctypes");
        sb.AppendLine("from typing import Any, Dict, Optional\n");
        sb.AppendLine("class DataWarehouseClient:");
        sb.AppendLine("    def __init__(self, lib_path: str):");
        sb.AppendLine("        self._lib = ctypes.CDLL(lib_path)\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"    def {ToSnakeCase(method.MethodName)}(self, {string.Join(", ", method.ParameterTypes.Select((t, i) => $"arg{i}: {MapType(t, LanguageTarget.Python)}"))}) -> {MapType(method.ReturnType, LanguageTarget.Python)}:");
            sb.AppendLine($"        \"\"\"" + method.Description + "\"\"\"");
            sb.AppendLine($"        pass  # Implementation via ctypes");
        }

        return Task.FromResult(sb.ToString());
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
}

/// <summary>Python pybind11 binding strategy.</summary>
public sealed class PythonPybind11Strategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "PythonPybind11",
        Description = "Python bindings using pybind11 for C++ interop with automatic type conversion",
        Category = SDKPortCategory.PythonBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python],
            SupportedTransports: [TransportType.FFI],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: true,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 5000),
        Tags = ["python", "pybind11", "cpp"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated pybind11 bindings");
        sb.AppendLine("#include <pybind11/pybind11.h>");
        sb.AppendLine("#include <pybind11/stl.h>\n");
        sb.AppendLine("namespace py = pybind11;\n");
        sb.AppendLine("PYBIND11_MODULE(datawarehouse, m) {");
        sb.AppendLine("    m.doc() = \"DataWarehouse Python bindings\";");

        foreach (var method in _registeredMethods.Values)
            sb.AppendLine($"    m.def(\"{ToSnakeCase(method.MethodName)}\", &{method.MethodName}, \"{method.Description}\");");

        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
}

/// <summary>Python gRPC binding strategy.</summary>
public sealed class PythonGrpcStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "PythonGrpc",
        Description = "Python bindings using gRPC for high-performance RPC",
        Category = SDKPortCategory.PythonBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python],
            SupportedTransports: [TransportType.GRPC],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: false,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 10000),
        Tags = ["python", "grpc", "rpc"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Auto-generated Python gRPC client");
        sb.AppendLine("import grpc");
        sb.AppendLine("from typing import Iterator, AsyncIterator\n");
        sb.AppendLine("class DataWarehouseClient:");
        sb.AppendLine("    def __init__(self, host: str = 'localhost', port: int = 50051):");
        sb.AppendLine("        self._channel = grpc.insecure_channel(f'{host}:{port}')");
        sb.AppendLine("        self._stub = DataWarehouseStub(self._channel)\n");

        foreach (var method in _registeredMethods.Values)
        {
            var asyncPrefix = method.IsAsync ? "async " : "";
            var returnType = method.IsStreaming ? $"AsyncIterator[{MapType(method.ReturnType, LanguageTarget.Python)}]" : MapType(method.ReturnType, LanguageTarget.Python);
            sb.AppendLine($"    {asyncPrefix}def {ToSnakeCase(method.MethodName)}(self, request) -> {returnType}:");
            sb.AppendLine($"        return self._stub.{method.MethodName}(request)");
        }

        return Task.FromResult(sb.ToString());
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
}

/// <summary>Python asyncio binding strategy.</summary>
public sealed class PythonAsyncioStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "PythonAsyncio",
        Description = "Python bindings with native asyncio support",
        Category = SDKPortCategory.PythonBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Python],
            SupportedTransports: [TransportType.REST, TransportType.WebSocket],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: true,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["python", "asyncio", "async"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Auto-generated Python asyncio client");
        sb.AppendLine("import asyncio");
        sb.AppendLine("import aiohttp");
        sb.AppendLine("from typing import Any, Dict, Optional, AsyncIterator\n");
        sb.AppendLine("class DataWarehouseAsyncClient:");
        sb.AppendLine("    def __init__(self, base_url: str = 'http://localhost:8080'):");
        sb.AppendLine("        self._base_url = base_url");
        sb.AppendLine("        self._session: Optional[aiohttp.ClientSession] = None\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"    async def {ToSnakeCase(method.MethodName)}(self, **kwargs) -> {MapType(method.ReturnType, LanguageTarget.Python)}:");
            sb.AppendLine($"        async with self._session.post(f'{{self._base_url}}/{method.MethodName}', json=kwargs) as resp:");
            sb.AppendLine("            return await resp.json()");
        }

        return Task.FromResult(sb.ToString());
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
}
