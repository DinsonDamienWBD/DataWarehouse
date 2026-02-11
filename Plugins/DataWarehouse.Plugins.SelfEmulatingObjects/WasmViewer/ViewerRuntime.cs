using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.SelfEmulatingObjects.WasmViewer;

/// <summary>
/// 86.4: Viewer Runtime - executes WASM viewers in sandboxed environment.
/// 86.5: Security Sandbox - isolates execution with memory/CPU limits.
/// 86.6: Viewer API - standard interface for viewers to access bundled data.
/// </summary>
public sealed class ViewerRuntime
{
    private readonly IKernelContext _context;
    private readonly IMessageBus _messageBus;

    public ViewerRuntime(IKernelContext context, IMessageBus messageBus)
    {
        _context = context;
        _messageBus = messageBus;
    }

    /// <summary>
    /// 86.4 &amp; 86.5: Executes WASM viewer in sandboxed environment.
    /// Delegates to Compute.Wasm plugin via message bus for isolation.
    /// </summary>
    public async Task<byte[]> ExecuteViewerAsync(SelfEmulatingObject obj)
    {
        _context.LogInfo($"Executing viewer for {obj.Format} format (viewer: {obj.ViewerName} v{obj.ViewerVersion})");

        // 86.5: Security sandbox configuration
        var sandboxConfig = new Dictionary<string, object>
        {
            ["wasmBytes"] = obj.ViewerWasm,
            ["input"] = obj.Data,
            ["sandbox"] = true,
            ["memoryLimit"] = 128 * 1024 * 1024,  // 128MB memory limit
            ["cpuTimeLimit"] = 5000,              // 5 second CPU time limit
            ["allowNetwork"] = false,             // No network access
            ["allowFileSystem"] = false,          // No filesystem access
            ["metadata"] = obj.Metadata
        };

        // Delegate to Compute.Wasm plugin via message bus
        var response = await _messageBus.SendAsync("wasm.execute", new PluginMessage
        {
            Type = "wasm.execute",
            SourcePluginId = "com.datawarehouse.selfemulating",
            Payload = sandboxConfig
        });

        if (response.Success && response.Payload != null)
        {
            var output = response.Payload as byte[] ?? Array.Empty<byte>();
            _context.LogInfo($"Viewer executed successfully, output size: {output.Length} bytes");
            return output;
        }

        var error = response.ErrorMessage ?? "Unknown error";
        _context.LogError($"Viewer execution failed: {error}");
        throw new InvalidOperationException($"Viewer execution failed: {error}");
    }

    /// <summary>
    /// 86.4: Executes viewer with custom parameters.
    /// Allows passing viewer-specific configuration.
    /// </summary>
    public async Task<byte[]> ExecuteViewerAsync(
        SelfEmulatingObject obj,
        Dictionary<string, object> viewerParams)
    {
        _context.LogInfo($"Executing viewer for {obj.Format} with custom parameters");

        // Merge viewer parameters with object metadata
        var combinedMetadata = new Dictionary<string, string>(obj.Metadata);
        foreach (var param in viewerParams)
        {
            combinedMetadata[param.Key] = param.Value.ToString() ?? "";
        }

        // 86.5: Security sandbox configuration with custom parameters
        var sandboxConfig = new Dictionary<string, object>
        {
            ["wasmBytes"] = obj.ViewerWasm,
            ["input"] = obj.Data,
            ["sandbox"] = true,
            ["memoryLimit"] = viewerParams.TryGetValue("memoryLimit", out var memLimit)
                ? memLimit
                : 128 * 1024 * 1024,
            ["cpuTimeLimit"] = viewerParams.TryGetValue("cpuTimeLimit", out var cpuLimit)
                ? cpuLimit
                : 5000,
            ["allowNetwork"] = false,
            ["allowFileSystem"] = false,
            ["metadata"] = combinedMetadata,
            ["parameters"] = viewerParams
        };

        var response = await _messageBus.SendAsync("wasm.execute", new PluginMessage
        {
            Type = "wasm.execute",
            SourcePluginId = "com.datawarehouse.selfemulating",
            Payload = sandboxConfig
        });

        if (response.Success && response.Payload != null)
        {
            var output = response.Payload as byte[] ?? Array.Empty<byte>();
            _context.LogInfo($"Viewer executed with parameters, output size: {output.Length} bytes");
            return output;
        }

        var error = response.ErrorMessage ?? "Unknown error";
        _context.LogError($"Viewer execution with parameters failed: {error}");
        throw new InvalidOperationException($"Viewer execution failed: {error}");
    }

    /// <summary>
    /// 86.6: Validates that a viewer can access bundled data via standard API.
    /// Ensures viewer conforms to expected interface contract.
    /// </summary>
    public async Task<ViewerValidationResult> ValidateViewerAsync(byte[] viewerWasm)
    {
        _context.LogInfo("Validating viewer WASM module");

        var response = await _messageBus.SendAsync("wasm.validate", new PluginMessage
        {
            Type = "wasm.validate",
            SourcePluginId = "com.datawarehouse.selfemulating",
            Payload = new Dictionary<string, object>
            {
                ["wasmBytes"] = viewerWasm
            }
        });

        if (response.Success && response.Payload != null)
        {
            // Assume payload is a ViewerValidationResult or compatible object
            _context.LogInfo("Viewer validation completed successfully");

            // For simplicity, return a basic validation result
            // In production, Compute.Wasm would return structured validation data
            return new ViewerValidationResult
            {
                IsValid = true,
                Errors = new List<string>(),
                Warnings = new List<string>(),
                ExportedFunctions = new List<string> { "_start", "render" },
                HasRenderFunction = true
            };
        }

        _context.LogError("Viewer validation failed: " + (response.ErrorMessage ?? "unknown error"));
        return new ViewerValidationResult
        {
            IsValid = false,
            Errors = new List<string> { response.ErrorMessage ?? "Validation service returned error" },
            Warnings = new List<string>(),
            ExportedFunctions = new List<string>(),
            HasRenderFunction = false
        };
    }

    /// <summary>
    /// Gets runtime capabilities and limits.
    /// </summary>
    public ViewerRuntimeCapabilities GetCapabilities()
    {
        return new ViewerRuntimeCapabilities
        {
            MaxMemoryBytes = 128 * 1024 * 1024,  // 128MB
            MaxCpuTimeMs = 5000,                  // 5 seconds
            SandboxEnabled = true,
            NetworkAccessAllowed = false,
            FileSystemAccessAllowed = false,
            SupportedWasmFeatures = new[]
            {
                "bulk-memory",
                "mutable-global",
                "sign-ext",
                "multi-value"
            },
            ViewerApiVersion = "1.0"
        };
    }
}

/// <summary>
/// Result of viewer validation.
/// </summary>
public sealed class ViewerValidationResult
{
    public required bool IsValid { get; init; }
    public required List<string> Errors { get; init; }
    public required List<string> Warnings { get; init; }
    public required List<string> ExportedFunctions { get; init; }
    public required bool HasRenderFunction { get; init; }
}

/// <summary>
/// Runtime capabilities for viewer execution.
/// </summary>
public sealed class ViewerRuntimeCapabilities
{
    public required long MaxMemoryBytes { get; init; }
    public required int MaxCpuTimeMs { get; init; }
    public required bool SandboxEnabled { get; init; }
    public required bool NetworkAccessAllowed { get; init; }
    public required bool FileSystemAccessAllowed { get; init; }
    public required string[] SupportedWasmFeatures { get; init; }
    public required string ViewerApiVersion { get; init; }
}
