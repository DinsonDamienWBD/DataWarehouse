using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateSDKPorts;

/// <summary>SDK port transport type.</summary>
public enum TransportType { FFI, GRPC, REST, WebSocket, MessageQueue, SharedMemory, Unix, Named }

/// <summary>SDK language target.</summary>
public enum LanguageTarget { Python, JavaScript, TypeScript, Go, Rust, Java, Ruby, CSharp, Swift, Kotlin, Cpp, C }

/// <summary>SDK port capabilities.</summary>
public sealed record SDKPortCapabilities(
    LanguageTarget[] SupportedLanguages,
    TransportType[] SupportedTransports,
    bool SupportsAsync,
    bool SupportsStreaming,
    bool SupportsCallbacks,
    bool SupportsBidirectional,
    bool SupportsAutoCodegen,
    bool SupportsTypeGeneration,
    int MaxConcurrentCalls = 1000);

/// <summary>SDK port characteristics.</summary>
public sealed record SDKPortCharacteristics
{
    public required string StrategyName { get; init; }
    public required string Description { get; init; }
    public required SDKPortCategory Category { get; init; }
    public required SDKPortCapabilities Capabilities { get; init; }
    public string[] Tags { get; init; } = Array.Empty<string>();
}

/// <summary>SDK port category.</summary>
public enum SDKPortCategory
{
    PythonBinding,
    JavaScriptBinding,
    GoBinding,
    RustBinding,
    JavaBinding,
    RubyBinding,
    CrossLanguage,
    Transport,
    CodeGeneration
}

/// <summary>SDK binding request.</summary>
public sealed class BindingRequest
{
    public required string OperationId { get; init; }
    public required string MethodName { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = new();
    public LanguageTarget SourceLanguage { get; init; }
    public TransportType Transport { get; init; } = TransportType.GRPC;
    public TimeSpan? Timeout { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>SDK binding response.</summary>
public sealed class BindingResponse
{
    public required string OperationId { get; init; }
    public bool Success { get; init; }
    public object? Result { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();

    public static BindingResponse Succeeded(string opId, object? result, TimeSpan duration) =>
        new() { OperationId = opId, Success = true, Result = result, Duration = duration };

    public static BindingResponse Failed(string opId, string error, TimeSpan duration) =>
        new() { OperationId = opId, Success = false, Error = error, Duration = duration };
}

/// <summary>SDK method registration.</summary>
public sealed class SDKMethod
{
    public required string MethodName { get; init; }
    public required string[] ParameterTypes { get; init; }
    public required string ReturnType { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool IsAsync { get; init; }
    public bool IsStreaming { get; init; }
    public Func<BindingRequest, CancellationToken, Task<BindingResponse>>? Handler { get; set; }
}

/// <summary>Type mapping definition.</summary>
public sealed class TypeMapping
{
    public required string SourceType { get; init; }
    public required Dictionary<LanguageTarget, string> TargetTypes { get; init; }
    public Func<object, LanguageTarget, object>? Converter { get; set; }
}

/// <summary>Base class for SDK port strategies.</summary>
public abstract class SDKPortStrategyBase
{
    protected readonly BoundedDictionary<string, SDKMethod> _registeredMethods = new BoundedDictionary<string, SDKMethod>(1000);
    protected readonly BoundedDictionary<string, TypeMapping> _typeMappings = new BoundedDictionary<string, TypeMapping>(1000);

    public abstract SDKPortCharacteristics Characteristics { get; }
    public string StrategyId => Characteristics.StrategyName.Replace(" ", "");

    public virtual void RegisterMethod(SDKMethod method) => _registeredMethods[method.MethodName] = method;
    public virtual void RegisterTypeMapping(TypeMapping mapping) => _typeMappings[mapping.SourceType] = mapping;

    public abstract Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default);
    public abstract Task<string> GenerateBindingCodeAsync(LanguageTarget language, CancellationToken ct = default);

    public virtual string MapType(string sourceType, LanguageTarget target)
    {
        if (_typeMappings.TryGetValue(sourceType, out var mapping) &&
            mapping.TargetTypes.TryGetValue(target, out var targetType))
            return targetType;
        return sourceType;
    }

    protected async Task<BindingResponse> ExecuteMethodAsync(BindingRequest request, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        if (!_registeredMethods.TryGetValue(request.MethodName, out var method))
            return BindingResponse.Failed(request.OperationId, $"Method '{request.MethodName}' not found", TimeSpan.Zero);

        if (method.Handler == null)
            return BindingResponse.Failed(request.OperationId, "No handler registered", TimeSpan.Zero);

        try
        {
            using var cts = request.Timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
            cts?.CancelAfter(request.Timeout!.Value);

            return await method.Handler(request, cts?.Token ?? ct);
        }
        catch (Exception ex)
        {
            return BindingResponse.Failed(request.OperationId, ex.Message, DateTime.UtcNow - start);
        }
    }
}
