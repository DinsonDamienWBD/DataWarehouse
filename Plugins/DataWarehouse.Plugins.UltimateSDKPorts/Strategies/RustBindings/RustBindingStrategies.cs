using System.Text;

namespace DataWarehouse.Plugins.UltimateSDKPorts.Strategies.RustBindings;

/// <summary>Rust FFI binding strategy.</summary>
public sealed class RustFfiStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "RustFfi",
        Description = "Rust bindings using FFI for direct C library integration with safety wrappers",
        Category = SDKPortCategory.RustBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Rust],
            SupportedTransports: [TransportType.FFI],
            SupportsAsync: false, SupportsStreaming: false, SupportsCallbacks: true,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["rust", "ffi", "unsafe"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Rust FFI bindings");
        sb.AppendLine("#![allow(non_camel_case_types)]\n");
        sb.AppendLine("use std::os::raw::*;\n");
        sb.AppendLine("extern \"C\" {");
        foreach (var method in _registeredMethods.Values)
        {
            var externParams = string.Join(", ", method.ParameterTypes.Select((t, i) => $"arg{i}: {MapType(t, LanguageTarget.Rust)}"));
            sb.AppendLine($"    fn {ToSnakeCase(method.MethodName)}({externParams}) -> {MapType(method.ReturnType, LanguageTarget.Rust)};");
        }
        sb.AppendLine("}\n");
        sb.AppendLine("/// Safe wrapper for DataWarehouse operations");
        sb.AppendLine("pub struct DataWarehouse;");
        sb.AppendLine("\nimpl DataWarehouse {");
        foreach (var method in _registeredMethods.Values)
        {
            var implParams = string.Join(", ", method.ParameterTypes.Select((t, i) => $"arg{i}: {MapType(t, LanguageTarget.Rust)}"));
            sb.AppendLine($"    /// {method.Description}");
            sb.AppendLine($"    pub fn {ToSnakeCase(method.MethodName)}({implParams}) -> Result<{MapType(method.ReturnType, LanguageTarget.Rust)}, Box<dyn std::error::Error>> {{");
            sb.AppendLine($"        unsafe {{ Ok({ToSnakeCase(method.MethodName)}({string.Join(", ", method.ParameterTypes.Select((_, i) => $"arg{i}"))})) }}");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
}

/// <summary>Rust Tokio async binding strategy.</summary>
public sealed class RustTokioStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "RustTokio",
        Description = "Rust bindings using Tokio for async/await operations",
        Category = SDKPortCategory.RustBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Rust],
            SupportedTransports: [TransportType.GRPC, TransportType.REST],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: true,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 10000),
        Tags = ["rust", "tokio", "async"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Rust Tokio async client");
        sb.AppendLine("use tokio::sync::mpsc;");
        sb.AppendLine("use serde::{Serialize, Deserialize};\n");
        sb.AppendLine("#[derive(Clone)]");
        sb.AppendLine("pub struct AsyncClient {");
        sb.AppendLine("    base_url: String,");
        sb.AppendLine("    client: reqwest::Client,");
        sb.AppendLine("}\n");
        sb.AppendLine("impl AsyncClient {");
        sb.AppendLine("    pub fn new(base_url: &str) -> Self {");
        sb.AppendLine("        Self { base_url: base_url.to_string(), client: reqwest::Client::new() }");
        sb.AppendLine("    }\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"    /// {method.Description}");
            sb.AppendLine($"    pub async fn {ToSnakeCase(method.MethodName)}<T: Serialize, R: for<'de> Deserialize<'de>>(&self, params: T) -> Result<R, reqwest::Error> {{");
            sb.AppendLine($"        self.client.post(format!(\"{{}}/api/{method.MethodName}\", self.base_url))");
            sb.AppendLine("            .json(&params).send().await?.json().await");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
}

/// <summary>Rust Tonic gRPC binding strategy.</summary>
public sealed class RustTonicStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "RustTonic",
        Description = "Rust bindings using Tonic for gRPC communication",
        Category = SDKPortCategory.RustBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Rust],
            SupportedTransports: [TransportType.GRPC],
            SupportsAsync: true, SupportsStreaming: true, SupportsCallbacks: false,
            SupportsBidirectional: true, SupportsAutoCodegen: true, SupportsTypeGeneration: true,
            MaxConcurrentCalls: 50000),
        Tags = ["rust", "tonic", "grpc"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Rust Tonic gRPC client");
        sb.AppendLine("use tonic::{transport::Channel, Request};\n");
        sb.AppendLine("pub mod proto { tonic::include_proto!(\"datawarehouse\"); }\n");
        sb.AppendLine("use proto::data_warehouse_client::DataWarehouseClient;\n");
        sb.AppendLine("pub struct GrpcClient { inner: DataWarehouseClient<Channel> }\n");
        sb.AppendLine("impl GrpcClient {");
        sb.AppendLine("    pub async fn connect(addr: &str) -> Result<Self, tonic::transport::Error> {");
        sb.AppendLine("        let inner = DataWarehouseClient::connect(addr.to_string()).await?;");
        sb.AppendLine("        Ok(Self { inner })");
        sb.AppendLine("    }\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine($"    pub async fn {ToSnakeCase(method.MethodName)}(&mut self, request: proto::{method.MethodName}Request) -> Result<proto::{method.MethodName}Response, tonic::Status> {{");
            sb.AppendLine($"        self.inner.{ToSnakeCase(method.MethodName)}(Request::new(request)).await.map(|r| r.into_inner())");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
}

/// <summary>Rust WebAssembly binding strategy.</summary>
public sealed class RustWasmStrategy : SDKPortStrategyBase
{
    public override SDKPortCharacteristics Characteristics { get; } = new()
    {
        StrategyName = "RustWasm",
        Description = "Rust bindings compiled to WebAssembly for browser/edge deployment",
        Category = SDKPortCategory.RustBinding,
        Capabilities = new(
            SupportedLanguages: [LanguageTarget.Rust, LanguageTarget.JavaScript],
            SupportedTransports: [TransportType.FFI],
            SupportsAsync: true, SupportsStreaming: false, SupportsCallbacks: true,
            SupportsBidirectional: false, SupportsAutoCodegen: true, SupportsTypeGeneration: true),
        Tags = ["rust", "wasm", "webassembly"]
    };

    public override async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default) =>
        await ExecuteMethodAsync(request, ct);

    public override Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated Rust WASM bindings");
        sb.AppendLine("use wasm_bindgen::prelude::*;");
        sb.AppendLine("use serde::{Serialize, Deserialize};\n");
        sb.AppendLine("#[wasm_bindgen]");
        sb.AppendLine("pub struct WasmClient { base_url: String }\n");
        sb.AppendLine("#[wasm_bindgen]");
        sb.AppendLine("impl WasmClient {");
        sb.AppendLine("    #[wasm_bindgen(constructor)]");
        sb.AppendLine("    pub fn new(base_url: &str) -> Self { Self { base_url: base_url.to_string() } }\n");

        foreach (var method in _registeredMethods.Values)
        {
            sb.AppendLine("    #[wasm_bindgen]");
            sb.AppendLine($"    pub async fn {ToSnakeCase(method.MethodName)}(&self, params: JsValue) -> Result<JsValue, JsValue> {{");
            sb.AppendLine("        // WASM binding implementation");
            sb.AppendLine("        Ok(JsValue::NULL)");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return Task.FromResult(sb.ToString());
    }

    private static string ToSnakeCase(string name) =>
        string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));
}
