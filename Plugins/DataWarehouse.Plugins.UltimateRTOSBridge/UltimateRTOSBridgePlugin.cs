using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateRTOSBridge;

/// <summary>
/// Ultimate RTOS Bridge plugin providing safety-critical system integration.
/// Intelligence-aware for predictive maintenance and anomaly detection in real-time systems.
/// </summary>
/// <remarks>
/// <para>
/// T144: UltimateRTOSBridge - Complete RTOS Integration Platform
/// </para>
/// <para>
/// RTOS Protocol Adapters (10 strategies):
/// - VxWorks: Wind River VxWorks with DO-178C DAL-A certification
/// - QNX Neutrino: BlackBerry QNX with ISO 26262 ASIL-D
/// - FreeRTOS: Amazon FreeRTOS/SAFERTOS with IEC 61508 SIL 3
/// - Zephyr: Linux Foundation Zephyr with IEC 61508 SIL 3
/// - INTEGRITY: Green Hills INTEGRITY with EAL 6+
/// - LynxOS: Lynx Software LynxOS with EAL 7
/// </para>
/// <para>
/// Deterministic I/O (4 strategies):
/// - Deterministic I/O with guaranteed latency
/// - Safety Certification with SIL/ASIL/DAL compliance
/// - Watchdog Integration with fault tolerance
/// - Priority Inversion Prevention
/// </para>
/// <para>
/// Safety Standards Supported:
/// - IEC 61508 SIL 1-4 (Industrial)
/// - DO-178C DAL A-E (Avionics)
/// - ISO 26262 ASIL A-D (Automotive)
/// - EN 50128 SIL 0-4 (Railway)
/// - IEC 62304 (Medical Devices)
/// - IEC 62443 (Industrial Security)
/// </para>
/// </remarks>
public sealed class UltimateRTOSBridgePlugin : IntelligenceAwarePluginBase, IDisposable
{
    private readonly ConcurrentDictionary<string, IRtosStrategy> _strategies = new();
    private IRtosStrategy? _defaultStrategy;
    private bool _initialized;
    private bool _disposed;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.rtos.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate RTOS Bridge";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IReadOnlyCollection<IRtosStrategy> GetStrategies() => _strategies.Values.ToList().AsReadOnly();

    /// <summary>
    /// Gets a strategy by ID.
    /// </summary>
    public IRtosStrategy? GetStrategy(string strategyId)
    {
        return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
    }

    /// <summary>
    /// Registers a strategy.
    /// </summary>
    public void RegisterStrategy(IRtosStrategy strategy)
    {
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Sets the default strategy.
    /// </summary>
    public void SetDefaultStrategy(string strategyId)
    {
        if (_strategies.TryGetValue(strategyId, out var strategy))
        {
            _defaultStrategy = strategy;
        }
    }

    /// <summary>
    /// Executes an RTOS operation using the specified or default strategy.
    /// </summary>
    public async Task<RtosOperationResult> ExecuteAsync(
        RtosOperationContext context,
        string? strategyId = null,
        CancellationToken cancellationToken = default)
    {
        var strategy = !string.IsNullOrEmpty(strategyId)
            ? GetStrategy(strategyId) ?? throw new ArgumentException($"Strategy '{strategyId}' not found")
            : _defaultStrategy ?? throw new InvalidOperationException("No default strategy configured");

        return await strategy.ExecuteAsync(context, cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        await base.StartAsync(ct);

        if (_initialized)
            return;

        await DiscoverAndRegisterStrategiesAsync(ct);

        // Set default strategy (prefer deterministic I/O)
        if (_strategies.TryGetValue("rtos-deterministic-io", out var deterministicIo))
        {
            _defaultStrategy = deterministicIo;
        }
        else if (_strategies.Any())
        {
            _defaultStrategy = _strategies.Values.First();
        }

        _initialized = true;
    }

    /// <summary>
    /// Called when Intelligence becomes available - register RTOS capabilities.
    /// </summary>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        if (MessageBus != null)
        {
            var strategyIds = _strategies.Keys.ToList();
            var platforms = _strategies.Values
                .SelectMany(s => s.Capabilities.SupportedPlatforms)
                .Distinct()
                .ToList();
            var standards = _strategies.Values
                .SelectMany(s => s.Capabilities.SupportedStandards)
                .Distinct()
                .ToList();

            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "rtos-bridge",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = _strategies.Count,
                        ["strategies"] = strategyIds,
                        ["platforms"] = platforms,
                        ["safetyStandards"] = standards,
                        ["supportsDeterministicIo"] = _strategies.Values.Any(s => s.Capabilities.SupportsDeterministicIo),
                        ["supportsSafetyCertifications"] = _strategies.Values.Any(s => s.Capabilities.SupportsSafetyCertifications),
                        ["supportsWatchdog"] = _strategies.Values.Any(s => s.Capabilities.SupportsWatchdog)
                    },
                    ["semanticDescription"] = $"Ultimate RTOS Bridge with {_strategies.Count} strategies. " +
                        $"Supports RTOS platforms: {string.Join(", ", platforms.Take(5))}. " +
                        $"Safety certifications: {string.Join(", ", standards.Take(5))}.",
                    ["tags"] = new[] { "rtos", "real-time", "safety-critical", "deterministic", "embedded" }
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task StopAsync()
    {
        _initialized = false;
        return Task.CompletedTask;
    }

    private async Task DiscoverAndRegisterStrategiesAsync(CancellationToken ct)
    {
        var strategyType = typeof(IRtosStrategy);
        var assembly = Assembly.GetExecutingAssembly();

        var types = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && strategyType.IsAssignableFrom(t));

        foreach (var type in types)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                if (Activator.CreateInstance(type) is IRtosStrategy strategy)
                {
                    await strategy.InitializeAsync(new Dictionary<string, object>(), ct);
                    _strategies[strategy.StrategyId] = strategy;
                }
            }
            catch
            {
                // Skip strategies that fail to initialize
            }
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<RegisteredCapability>
            {
                new()
                {
                    CapabilityId = "rtos.ultimate",
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    DisplayName = "Ultimate RTOS Bridge",
                    Description = "Safety-critical RTOS integration with deterministic I/O and safety certifications",
                    Category = SDK.Contracts.CapabilityCategory.Infrastructure,
                    Tags = ["rtos", "real-time", "safety-critical", "embedded", "deterministic"]
                }
            };

            foreach (var (strategyId, strategy) in _strategies)
            {
                var tags = new List<string> { "rtos", strategyId };
                tags.AddRange(strategy.Capabilities.SupportedStandards.Take(3));

                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"rtos.{strategyId}",
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    DisplayName = strategy.StrategyName,
                    Description = $"RTOS strategy: {strategy.StrategyName}",
                    Category = SDK.Contracts.CapabilityCategory.Infrastructure,
                    Tags = tags.ToArray()
                });
            }

            return capabilities.AsReadOnly();
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var strategyIds = _strategies.Keys.ToList();
        var platforms = _strategies.Values
            .SelectMany(s => s.Capabilities.SupportedPlatforms)
            .Distinct()
            .ToList();
        var standards = _strategies.Values
            .SelectMany(s => s.Capabilities.SupportedStandards)
            .Distinct()
            .ToList();

        return new List<KnowledgeObject>
        {
            new()
            {
                Id = $"{Id}:overview",
                Topic = "rtos-bridge",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Ultimate RTOS Bridge Plugin provides {_strategies.Count} safety-critical integration strategies. " +
                              $"Supported RTOS platforms: {string.Join(", ", platforms)}. " +
                              $"Safety standards: {string.Join(", ", standards)}. " +
                              "Features include deterministic I/O with guaranteed latency, safety certification compliance " +
                              "(IEC 61508, DO-178C, ISO 26262), watchdog integration, and priority inversion prevention.",
                Tags = ["rtos", "real-time", "safety-critical", "deterministic", "embedded", "iec61508", "do178c", "iso26262"],
                Payload = new Dictionary<string, object>
                {
                    ["strategyCount"] = _strategies.Count,
                    ["strategies"] = strategyIds,
                    ["platforms"] = platforms,
                    ["safetyStandards"] = standards,
                    ["defaultStrategy"] = _defaultStrategy?.StrategyId ?? "none"
                }
            }
        };
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "RTOSBridge";
        metadata["SupportsAutoDiscovery"] = true;
        metadata["RegisteredStrategies"] = _strategies.Count;
        metadata["DefaultStrategy"] = _defaultStrategy?.StrategyId ?? "none";
        metadata["SupportedPlatforms"] = _strategies.Values
            .SelectMany(s => s.Capabilities.SupportedPlatforms)
            .Distinct()
            .ToList();
        metadata["SupportedStandards"] = _strategies.Values
            .SelectMany(s => s.Capabilities.SupportedStandards)
            .Distinct()
            .ToList();
        return metadata;
    }

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "rtos.execute":
                await HandleExecuteAsync(message);
                break;

            case "rtos.list-strategies":
                HandleListStrategies(message);
                break;

            case "rtos.set-default":
                HandleSetDefault(message);
                break;

            case "rtos.get-platforms":
                HandleGetPlatforms(message);
                break;

            case "rtos.get-standards":
                HandleGetStandards(message);
                break;
        }

        await base.OnMessageAsync(message);
    }

    private async Task HandleExecuteAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("Context", out var contextObj) ||
            contextObj is not RtosOperationContext context)
        {
            message.Payload["Error"] = "Missing or invalid Context";
            return;
        }

        var strategyId = message.Payload.TryGetValue("StrategyId", out var sidObj) && sidObj is string sid
            ? sid
            : null;

        var result = await ExecuteAsync(context, strategyId);
        message.Payload["Result"] = result;
    }

    private void HandleListStrategies(PluginMessage message)
    {
        var strategies = _strategies.Values.Select(s => new Dictionary<string, object>
        {
            ["Id"] = s.StrategyId,
            ["Name"] = s.StrategyName,
            ["SupportsDeterministicIo"] = s.Capabilities.SupportsDeterministicIo,
            ["SupportsSafetyCertifications"] = s.Capabilities.SupportsSafetyCertifications,
            ["MaxLatencyMicroseconds"] = s.Capabilities.MaxGuaranteedLatencyMicroseconds,
            ["SupportedPlatforms"] = s.Capabilities.SupportedPlatforms,
            ["SupportedStandards"] = s.Capabilities.SupportedStandards
        }).ToList();

        message.Payload["Strategies"] = strategies;
        message.Payload["Count"] = strategies.Count;
    }

    private void HandleSetDefault(PluginMessage message)
    {
        if (message.Payload.TryGetValue("StrategyId", out var sidObj) && sidObj is string strategyId)
        {
            SetDefaultStrategy(strategyId);
            message.Payload["Success"] = true;
            message.Payload["DefaultStrategy"] = _defaultStrategy?.StrategyId ?? "none";
        }
        else
        {
            message.Payload["Success"] = false;
            message.Payload["Error"] = "Missing StrategyId";
        }
    }

    private void HandleGetPlatforms(PluginMessage message)
    {
        var platforms = _strategies.Values
            .SelectMany(s => s.Capabilities.SupportedPlatforms)
            .Distinct()
            .ToList();

        message.Payload["Platforms"] = platforms;
    }

    private void HandleGetStandards(PluginMessage message)
    {
        var standards = _strategies.Values
            .SelectMany(s => s.Capabilities.SupportedStandards)
            .Distinct()
            .ToList();

        message.Payload["Standards"] = standards;
    }

    /// <inheritdoc/>
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        if (request.Config != null &&
            request.Config.TryGetValue("MessageBus", out var mbObj) &&
            mbObj is IMessageBus messageBus)
        {
            SetMessageBus(messageBus);
        }

        return base.OnHandshakeAsync(request);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _strategies.Clear();
        }
        base.Dispose(disposing);
    }
}
