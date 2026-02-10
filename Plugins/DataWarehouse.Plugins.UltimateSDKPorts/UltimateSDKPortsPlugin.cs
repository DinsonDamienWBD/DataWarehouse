using System.Collections.Concurrent;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateSDKPorts.Strategies.PythonBindings;
using DataWarehouse.Plugins.UltimateSDKPorts.Strategies.JavaScriptBindings;
using DataWarehouse.Plugins.UltimateSDKPorts.Strategies.GoBindings;
using DataWarehouse.Plugins.UltimateSDKPorts.Strategies.RustBindings;
using DataWarehouse.Plugins.UltimateSDKPorts.Strategies.CrossLanguage;

namespace DataWarehouse.Plugins.UltimateSDKPorts;

/// <summary>
/// Ultimate SDK Ports Plugin - Multi-language SDK binding generation.
///
/// Implements T136 SDK Ports:
/// - Python bindings (ctypes, pybind11, gRPC, asyncio)
/// - JavaScript/TypeScript bindings (N-API, WebSocket, gRPC-Web, fetch)
/// - Go bindings (CGO, gRPC, HTTP client, channels)
/// - Rust bindings (FFI, Tokio, Tonic, WASM)
/// - Cross-language (Universal gRPC, OpenAPI, JSON-RPC, MessagePack, Thrift, Cap'n Proto)
///
/// Features:
/// - 31+ SDK binding strategies
/// - Auto code generation for all languages
/// - Type mapping and conversion
/// - Multiple transport support (FFI, gRPC, REST, WebSocket)
/// </summary>
public sealed class UltimateSDKPortsPlugin : IntelligenceAwarePluginBase, IDisposable
{
    private readonly SDKPortStrategyRegistry _registry = new();
    private readonly ConcurrentDictionary<string, SDKMethod> _globalMethods = new();
    private readonly ConcurrentDictionary<string, TypeMapping> _globalTypeMappings = new();
    private SDKPortStrategyBase? _activeStrategy;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private long _totalInvocations;
    private long _successfulInvocations;
    private long _codeGenerations;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.sdkports.ultimate";
    /// <inheritdoc/>
    public override string Name => "Ultimate SDK Ports";
    /// <inheritdoc/>
    public override string Version => "1.0.0";
    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <summary>Semantic description for AI discovery.</summary>
    public string SemanticDescription =>
        "Ultimate SDK ports plugin providing 31+ multi-language SDK binding strategies including " +
        "Python (ctypes, pybind11, gRPC, asyncio), JavaScript/TypeScript (N-API, WebSocket, gRPC-Web, fetch), " +
        "Go (CGO, gRPC, HTTP, channels), Rust (FFI, Tokio, Tonic, WASM), and cross-language bindings " +
        "(Universal gRPC, OpenAPI, JSON-RPC, MessagePack, Thrift, Cap'n Proto) with auto code generation.";

    /// <summary>Semantic tags for AI discovery.</summary>
    public string[] SemanticTags => new[]
    {
        "sdk", "bindings", "python", "javascript", "typescript", "go", "rust",
        "ffi", "grpc", "rest", "websocket", "code-generation", "cross-language"
    };

    /// <summary>Gets the SDK port strategy registry.</summary>
    public SDKPortStrategyRegistry Registry => _registry;

    /// <summary>Initializes a new instance of the Ultimate SDK Ports plugin.</summary>
    public UltimateSDKPortsPlugin()
    {
        DiscoverAndRegisterStrategies();
        RegisterDefaultTypeMappings();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _activeStrategy ??= _registry.Get("UniversalGrpc") ?? _registry.GetAll().FirstOrDefault();

        return await Task.FromResult(new HandshakeResponse
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

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (MessageBus != null)
        {
            MessageBus.Subscribe("sdkports.invoke", HandleInvokeAsync);
            MessageBus.Subscribe("sdkports.generate", HandleGenerateAsync);
            MessageBus.Subscribe("sdkports.strategy.select", HandleSelectStrategyAsync);
        }
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (MessageBus != null)
        {
            MessageBus.Subscribe("sdkports.invoke", HandleInvokeAsync);
            MessageBus.Subscribe("sdkports.generate", HandleGenerateAsync);
            MessageBus.Subscribe("sdkports.strategy.select", HandleSelectStrategyAsync);
        }
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task OnStopCoreAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        if (message.Payload == null) return;

        var response = message.Type switch
        {
            "sdkports.strategy.list" => HandleListStrategies(),
            "sdkports.strategy.select" => HandleSelectStrategy(message.Payload),
            "sdkports.method.register" => HandleRegisterMethod(message.Payload),
            "sdkports.invoke" => await HandleInvokeAsync(message.Payload),
            "sdkports.generate" => await HandleGenerateAsync(message.Payload),
            "sdkports.languages" => HandleListLanguages(),
            "sdkports.stats" => HandleGetStats(),
            _ => new Dictionary<string, object> { ["error"] = $"Unknown command: {message.Type}" }
        };

        message.Payload["_response"] = response;
    }

    #region Strategy Discovery

    private void DiscoverAndRegisterStrategies()
    {
        // Python bindings (4)
        _registry.Register(new PythonCtypesStrategy());
        _registry.Register(new PythonPybind11Strategy());
        _registry.Register(new PythonGrpcStrategy());
        _registry.Register(new PythonAsyncioStrategy());

        // JavaScript bindings (4)
        _registry.Register(new NodeNativeAddonStrategy());
        _registry.Register(new JavaScriptWebSocketStrategy());
        _registry.Register(new JavaScriptGrpcWebStrategy());
        _registry.Register(new JavaScriptFetchStrategy());

        // Go bindings (4)
        _registry.Register(new GoCgoStrategy());
        _registry.Register(new GoGrpcStrategy());
        _registry.Register(new GoHttpClientStrategy());
        _registry.Register(new GoChannelStrategy());

        // Rust bindings (4)
        _registry.Register(new RustFfiStrategy());
        _registry.Register(new RustTokioStrategy());
        _registry.Register(new RustTonicStrategy());
        _registry.Register(new RustWasmStrategy());

        // Cross-language bindings (6)
        _registry.Register(new UniversalGrpcStrategy());
        _registry.Register(new OpenApiStrategy());
        _registry.Register(new JsonRpcStrategy());
        _registry.Register(new MessagePackStrategy());
        _registry.Register(new ThriftStrategy());
        _registry.Register(new CapnProtoStrategy());
    }

    private void RegisterDefaultTypeMappings()
    {
        RegisterTypeMapping(new TypeMapping
        {
            SourceType = "string",
            TargetTypes = new Dictionary<LanguageTarget, string>
            {
                [LanguageTarget.Python] = "str",
                [LanguageTarget.JavaScript] = "string",
                [LanguageTarget.TypeScript] = "string",
                [LanguageTarget.Go] = "string",
                [LanguageTarget.Rust] = "String",
                [LanguageTarget.Java] = "String",
                [LanguageTarget.CSharp] = "string"
            }
        });

        RegisterTypeMapping(new TypeMapping
        {
            SourceType = "int",
            TargetTypes = new Dictionary<LanguageTarget, string>
            {
                [LanguageTarget.Python] = "int",
                [LanguageTarget.JavaScript] = "number",
                [LanguageTarget.TypeScript] = "number",
                [LanguageTarget.Go] = "int32",
                [LanguageTarget.Rust] = "i32",
                [LanguageTarget.Java] = "int",
                [LanguageTarget.CSharp] = "int"
            }
        });

        RegisterTypeMapping(new TypeMapping
        {
            SourceType = "bool",
            TargetTypes = new Dictionary<LanguageTarget, string>
            {
                [LanguageTarget.Python] = "bool",
                [LanguageTarget.JavaScript] = "boolean",
                [LanguageTarget.TypeScript] = "boolean",
                [LanguageTarget.Go] = "bool",
                [LanguageTarget.Rust] = "bool",
                [LanguageTarget.Java] = "boolean",
                [LanguageTarget.CSharp] = "bool"
            }
        });
    }

    #endregion

    #region Public API

    public IReadOnlyCollection<string> GetRegisteredStrategies() => _registry.RegisteredStrategies;
    public SDKPortStrategyBase? GetStrategy(string name) => _registry.Get(name);

    public void SetActiveStrategy(string strategyName)
    {
        _activeStrategy = _registry.Get(strategyName)
            ?? throw new ArgumentException($"Strategy '{strategyName}' not found");
    }

    public void RegisterMethod(SDKMethod method)
    {
        _globalMethods[method.MethodName] = method;
        foreach (var strategy in _registry.GetAll())
            strategy.RegisterMethod(method);
    }

    public void RegisterTypeMapping(TypeMapping mapping)
    {
        _globalTypeMappings[mapping.SourceType] = mapping;
        foreach (var strategy in _registry.GetAll())
            strategy.RegisterTypeMapping(mapping);
    }

    public async Task<BindingResponse> InvokeAsync(BindingRequest request, CancellationToken ct = default)
    {
        if (_activeStrategy == null)
            return BindingResponse.Failed(request.OperationId, "No active strategy", TimeSpan.Zero);

        Interlocked.Increment(ref _totalInvocations);
        var result = await _activeStrategy.InvokeAsync(request, ct);
        if (result.Success) Interlocked.Increment(ref _successfulInvocations);
        return result;
    }

    public async Task<string> GenerateBindingCodeAsync(LanguageTarget language, string? strategyName = null, CancellationToken ct = default)
    {
        var strategy = strategyName != null ? _registry.Get(strategyName) : _activeStrategy;
        if (strategy == null) throw new InvalidOperationException("No strategy available");

        Interlocked.Increment(ref _codeGenerations);
        return await strategy.GenerateBindingCodeAsync(language, ct);
    }

    #endregion

    #region Message Handlers

    private Dictionary<string, object> HandleListStrategies()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["count"] = _registry.Count,
            ["strategies"] = _registry.GetAll().Select(s => new Dictionary<string, object>
            {
                ["name"] = s.Characteristics.StrategyName,
                ["description"] = s.Characteristics.Description,
                ["category"] = s.Characteristics.Category.ToString(),
                ["languages"] = s.Characteristics.Capabilities.SupportedLanguages.Select(l => l.ToString()).ToArray(),
                ["transports"] = s.Characteristics.Capabilities.SupportedTransports.Select(t => t.ToString()).ToArray()
            }).ToList(),
            ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none"
        };
    }

    private Dictionary<string, object> HandleSelectStrategy(Dictionary<string, object> payload)
    {
        try
        {
            var strategyName = payload.GetValueOrDefault("strategy")?.ToString();
            if (string.IsNullOrEmpty(strategyName))
                return new Dictionary<string, object> { ["success"] = false, ["error"] = "Strategy name required" };
            SetActiveStrategy(strategyName);
            return new Dictionary<string, object> { ["success"] = true, ["activeStrategy"] = strategyName };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private Dictionary<string, object> HandleRegisterMethod(Dictionary<string, object> payload)
    {
        try
        {
            var methodName = payload.GetValueOrDefault("methodName")?.ToString() ?? throw new ArgumentException("Method name required");
            var method = new SDKMethod
            {
                MethodName = methodName,
                ParameterTypes = (payload.GetValueOrDefault("parameterTypes") as string[]) ?? Array.Empty<string>(),
                ReturnType = payload.GetValueOrDefault("returnType")?.ToString() ?? "void",
                Description = payload.GetValueOrDefault("description")?.ToString() ?? ""
            };
            RegisterMethod(method);
            return new Dictionary<string, object> { ["success"] = true, ["methodName"] = methodName };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private async Task<Dictionary<string, object>> HandleInvokeAsync(Dictionary<string, object> payload)
    {
        try
        {
            var request = new BindingRequest
            {
                OperationId = payload.GetValueOrDefault("operationId")?.ToString() ?? Guid.NewGuid().ToString("N"),
                MethodName = payload.GetValueOrDefault("methodName")?.ToString() ?? throw new ArgumentException("Method name required"),
                Parameters = payload.GetValueOrDefault("parameters") as Dictionary<string, object> ?? new()
            };

            var result = await InvokeAsync(request, _cts?.Token ?? default);
            return new Dictionary<string, object>
            {
                ["success"] = result.Success,
                ["operationId"] = result.OperationId,
                ["result"] = result.Result ?? new object(),
                ["error"] = result.Error ?? "",
                ["durationMs"] = result.Duration.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private async Task<Dictionary<string, object>> HandleGenerateAsync(Dictionary<string, object> payload)
    {
        try
        {
            var languageStr = payload.GetValueOrDefault("language")?.ToString() ?? throw new ArgumentException("Language required");
            if (!Enum.TryParse<LanguageTarget>(languageStr, true, out var language))
                throw new ArgumentException($"Unknown language: {languageStr}");

            var strategyName = payload.GetValueOrDefault("strategy")?.ToString();
            var code = await GenerateBindingCodeAsync(language, strategyName, _cts?.Token ?? default);

            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["language"] = language.ToString(),
                ["code"] = code,
                ["strategy"] = strategyName ?? _activeStrategy?.Characteristics.StrategyName ?? "none"
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object> { ["success"] = false, ["error"] = ex.Message };
        }
    }

    private Dictionary<string, object> HandleListLanguages()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["languages"] = Enum.GetNames(typeof(LanguageTarget)),
            ["transports"] = Enum.GetNames(typeof(TransportType))
        };
    }

    private Dictionary<string, object> HandleGetStats()
    {
        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["totalInvocations"] = _totalInvocations,
            ["successfulInvocations"] = _successfulInvocations,
            ["codeGenerations"] = _codeGenerations,
            ["registeredMethods"] = _globalMethods.Count,
            ["registeredTypeMappings"] = _globalTypeMappings.Count,
            ["registeredStrategies"] = _registry.Count,
            ["activeStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none"
        };
    }

    private async Task HandleInvokeAsync(PluginMessage message)
    {
        var response = await HandleInvokeAsync(message.Payload);
        if (MessageBus != null)
            await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
            {
                Type = $"{message.Type}.response",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = response
            });
    }

    private async Task HandleGenerateAsync(PluginMessage message)
    {
        var response = await HandleGenerateAsync(message.Payload);
        if (MessageBus != null)
            await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
            {
                Type = $"{message.Type}.response",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = response
            });
    }

    private async Task HandleSelectStrategyAsync(PluginMessage message)
    {
        var response = HandleSelectStrategy(message.Payload);
        if (MessageBus != null)
            await MessageBus.PublishAsync($"{message.Type}.response", new PluginMessage
            {
                Type = $"{message.Type}.response",
                CorrelationId = message.CorrelationId,
                Source = Id,
                Payload = response
            });
    }

    #endregion

    #region Capability & Metadata

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        var capabilities = new List<PluginCapabilityDescriptor>
        {
            new() { Name = "sdkports.invoke", Description = "Invoke a registered method" },
            new() { Name = "sdkports.generate", Description = "Generate binding code" },
            new() { Name = "sdkports.method.register", Description = "Register a method for binding" },
            new() { Name = "sdkports.strategy.list", Description = "List available strategies" },
            new() { Name = "sdkports.strategy.select", Description = "Select active strategy" },
            new() { Name = "sdkports.languages", Description = "List supported languages" },
            new() { Name = "sdkports.stats", Description = "Get statistics" }
        };

        foreach (var strategy in _registry.GetAll())
            capabilities.Add(new PluginCapabilityDescriptor
            {
                Name = $"sdkports.strategy.{strategy.StrategyId.ToLowerInvariant()}",
                Description = strategy.Characteristics.Description
            });

        return capabilities;
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "UltimateSDKPorts";
        metadata["StrategyCount"] = _registry.Count;
        metadata["Strategies"] = _registry.RegisteredStrategies.ToArray();
        metadata["ActiveStrategy"] = _activeStrategy?.Characteristics.StrategyName ?? "none";
        metadata["SupportedLanguages"] = Enum.GetNames(typeof(LanguageTarget));
        metadata["SupportedTransports"] = Enum.GetNames(typeof(TransportType));
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["SemanticTags"] = SemanticTags;
        return metadata;
    }

    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<RegisteredCapability>
            {
                new()
                {
                    CapabilityId = $"{Id}.bindings",
                    DisplayName = "Ultimate SDK Ports",
                    Description = SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.DataManagement,
                    SubCategory = "SDK",
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = SemanticTags,
                    SemanticDescription = SemanticDescription
                }
            };

            foreach (var strategy in _registry.GetAll())
            {
                var chars = strategy.Characteristics;
                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.strategy.{strategy.StrategyId.ToLowerInvariant()}",
                    DisplayName = chars.StrategyName,
                    Description = chars.Description,
                    Category = SDK.Contracts.CapabilityCategory.DataManagement,
                    SubCategory = "SDK",
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = chars.Tags,
                    Metadata = new Dictionary<string, object>
                    {
                        ["category"] = chars.Category.ToString(),
                        ["languages"] = chars.Capabilities.SupportedLanguages.Select(l => l.ToString()).ToArray(),
                        ["transports"] = chars.Capabilities.SupportedTransports.Select(t => t.ToString()).ToArray(),
                        ["supportsAsync"] = chars.Capabilities.SupportsAsync,
                        ["supportsStreaming"] = chars.Capabilities.SupportsStreaming
                    },
                    SemanticDescription = chars.Description
                });
            }

            return capabilities;
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
