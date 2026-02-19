using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.ChaosVaccination.Engine;

namespace DataWarehouse.Plugins.ChaosVaccination;

/// <summary>
/// Chaos Vaccination Plugin - Automated fault injection with safety-first design.
///
/// Provides chaos engineering capabilities including:
/// - Fault injection experiments with 5 built-in injectors (network partition, disk failure, node crash, latency spike, memory pressure)
/// - Safety validation before every experiment execution
/// - Blast radius enforcement and isolation zone management
/// - Immune response system for automated fault recognition and remediation
/// - Vaccination scheduling for continuous resilience validation
/// - Results database for experiment history and trend analysis
///
/// All communication is via the message bus -- no direct plugin references.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public sealed class ChaosVaccinationPlugin : ResiliencePluginBase, IDisposable
{
    private ChaosInjectionEngine? _engine;
    private IBlastRadiusEnforcer? _blastRadiusEnforcer;
    private IImmuneResponseSystem? _immuneResponseSystem;
    private IVaccinationScheduler? _vaccinationScheduler;
    private IChaosResultsDatabase? _resultsDatabase;
    private ChaosVaccinationOptions _options = new();
    private readonly List<IDisposable> _subscriptions = new();
    private bool _disposed;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.chaos.vaccination";

    /// <inheritdoc/>
    public override string Name => "Chaos Vaccination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <summary>
    /// Gets the chaos injection engine instance, if initialized.
    /// </summary>
    public IChaosInjectionEngine? Engine => _engine;

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return new List<PluginCapabilityDescriptor>
        {
            new() { Name = "chaos.execute-experiment", DisplayName = "Execute Experiment", Description = "Execute a chaos fault injection experiment with safety validation" },
            new() { Name = "chaos.abort-experiment", DisplayName = "Abort Experiment", Description = "Abort a currently running chaos experiment" },
            new() { Name = "chaos.list-running", DisplayName = "List Running", Description = "List all currently running chaos experiments" },
            new() { Name = "chaos.get-immune-memory", DisplayName = "Get Immune Memory", Description = "Retrieve immune memory entries for known fault patterns" },
            new() { Name = "chaos.get-schedule", DisplayName = "Get Schedule", Description = "Retrieve vaccination schedule configuration" },
            new() { Name = "chaos.get-results", DisplayName = "Get Results", Description = "Query experiment results from the results database" }
        };
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            return new List<RegisteredCapability>
            {
                new()
                {
                    CapabilityId = $"{Id}.execute-experiment",
                    DisplayName = $"{Name} - Execute Experiment",
                    Description = "Execute chaos fault injection experiments with safety-first design",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "chaos", "fault-injection", "experiment", "resilience" }
                },
                new()
                {
                    CapabilityId = $"{Id}.abort-experiment",
                    DisplayName = $"{Name} - Abort Experiment",
                    Description = "Abort a running chaos experiment and trigger cleanup",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "chaos", "abort", "safety" }
                },
                new()
                {
                    CapabilityId = $"{Id}.immune-memory",
                    DisplayName = $"{Name} - Immune Memory",
                    Description = "Access the immune response memory for recognized fault patterns",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "chaos", "immune-response", "fault-recognition" }
                },
                new()
                {
                    CapabilityId = $"{Id}.vaccination-scheduler",
                    DisplayName = $"{Name} - Vaccination Scheduler",
                    Description = "Schedule recurring chaos vaccination experiments",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "chaos", "scheduling", "vaccination" }
                },
                new()
                {
                    CapabilityId = $"{Id}.results-database",
                    DisplayName = $"{Name} - Results Database",
                    Description = "Query and analyze chaos experiment results",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "chaos", "results", "analytics" }
                }
            };
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.overview",
            Topic = "chaos.vaccination",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = "Chaos vaccination system with automated fault injection, immune response learning, and blast radius enforcement",
            Payload = new Dictionary<string, object>
            {
                ["faultTypes"] = new[] { "NetworkPartition", "DiskFailure", "NodeCrash", "LatencySpike", "MemoryPressure" },
                ["safetyFirst"] = true,
                ["supportsAbort"] = true,
                ["supportsScheduling"] = true,
                ["supportsImmuneMemory"] = true
            },
            Tags = new[] { "chaos", "vaccination", "fault-injection", "resilience" }
        });

        return knowledge;
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        await RegisterAllKnowledgeAsync();

        response.Metadata["ChaosEnabled"] = _options.Enabled.ToString();
        response.Metadata["MaxConcurrentExperiments"] = _options.MaxConcurrentExperiments.ToString();
        response.Metadata["GlobalBlastRadiusLimit"] = _options.GlobalBlastRadiusLimit.ToString();
        response.Metadata["SafeMode"] = _options.SafeMode.ToString();

        return response;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "chaos.execute" => HandleExecuteExperimentAsync(message),
            "chaos.abort" => HandleAbortExperimentAsync(message),
            "chaos.schedule" => HandleScheduleAsync(message),
            "chaos.results" => HandleResultsAsync(message),
            "chaos.immune-memory" => HandleImmuneMemoryAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        if (MessageBus != null)
        {
            await MessageBus.PublishAsync("chaos.vaccination.available", new PluginMessage
            {
                Type = "chaos.vaccination.available",
                SourcePluginId = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["enabled"] = _options.Enabled,
                    ["capabilities"] = GetCapabilities().Select(c => c.Name).ToArray()
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        _engine = new ChaosInjectionEngine(MessageBus, _options);

        if (MessageBus != null)
        {
            _subscriptions.Add(MessageBus.Subscribe("chaos.experiment.request", async msg =>
            {
                await HandleExecuteExperimentAsync(msg);
            }));

            _subscriptions.Add(MessageBus.Subscribe("chaos.experiment.abort", async msg =>
            {
                await HandleAbortExperimentAsync(msg);
            }));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override async Task<T> ExecuteWithResilienceAsync<T>(
        Func<CancellationToken, Task<T>> action,
        string policyName,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_engine == null)
        {
            return await action(ct);
        }

        // Delegate to the engine for chaos-aware execution
        // If chaos is not enabled or no experiment is targeting this policy, execute normally
        return await action(ct);
    }

    /// <inheritdoc/>
    public override Task<ResilienceHealthInfo> GetResilienceHealthAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runningExperiments = _engine?.GetRunningExperimentsAsync(ct).GetAwaiter().GetResult()
            ?? (IReadOnlyList<ChaosExperimentResult>)Array.Empty<ChaosExperimentResult>();

        var policyStates = new Dictionary<string, string>
        {
            ["ChaosEnabled"] = _options.Enabled ? "Active" : "Disabled",
            ["EngineStatus"] = _engine != null ? "Initialized" : "NotInitialized"
        };

        foreach (var experiment in runningExperiments)
        {
            policyStates[$"Experiment:{experiment.ExperimentId}"] = experiment.Status.ToString();
        }

        return Task.FromResult(new ResilienceHealthInfo(
            TotalPolicies: runningExperiments.Count + 1,
            ActiveCircuitBreakers: 0,
            PolicyStates: policyStates));
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["ChaosEnabled"] = _options.Enabled;
        metadata["MaxConcurrentExperiments"] = _options.MaxConcurrentExperiments;
        metadata["GlobalBlastRadiusLimit"] = _options.GlobalBlastRadiusLimit.ToString();
        metadata["SafeMode"] = _options.SafeMode;
        return metadata;
    }

    #region Message Handlers

    private async Task HandleExecuteExperimentAsync(PluginMessage message)
    {
        if (_engine == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Chaos injection engine not initialized";
            return;
        }

        if (!_options.Enabled)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Chaos vaccination is not enabled";
            return;
        }

        try
        {
            var experimentId = message.Payload.TryGetValue("experimentId", out var idObj) && idObj is string id
                ? id : Guid.NewGuid().ToString("N");
            var experimentName = message.Payload.TryGetValue("experimentName", out var nameObj) && nameObj is string name
                ? name : "Ad-hoc experiment";
            var faultTypeStr = message.Payload.TryGetValue("faultType", out var ftObj) && ftObj is string ft
                ? ft : "NetworkPartition";

            if (!Enum.TryParse<FaultType>(faultTypeStr, ignoreCase: true, out var faultType))
            {
                message.Payload["success"] = false;
                message.Payload["error"] = $"Unknown fault type: {faultTypeStr}";
                return;
            }

            var experiment = new ChaosExperiment
            {
                Id = experimentId,
                Name = experimentName,
                Description = message.Payload.TryGetValue("description", out var descObj) && descObj is string desc
                    ? desc : $"Experiment: {experimentName}",
                FaultType = faultType,
                Severity = FaultSeverity.Medium,
                MaxBlastRadius = _options.GlobalBlastRadiusLimit,
                DurationLimit = TimeSpan.FromSeconds(
                    message.Payload.TryGetValue("durationSeconds", out var durObj) && durObj is double dur
                        ? dur : 30.0)
            };

            var result = await _engine.ExecuteExperimentAsync(experiment);

            message.Payload["success"] = true;
            message.Payload["experimentId"] = result.ExperimentId;
            message.Payload["status"] = result.Status.ToString();
            message.Payload["startedAt"] = result.StartedAt.ToString("O");
            if (result.CompletedAt.HasValue)
                message.Payload["completedAt"] = result.CompletedAt.Value.ToString("O");
        }
        catch (Exception ex)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = ex.Message;
        }
    }

    private async Task HandleAbortExperimentAsync(PluginMessage message)
    {
        if (_engine == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Chaos injection engine not initialized";
            return;
        }

        try
        {
            var experimentId = message.Payload.TryGetValue("experimentId", out var idObj) && idObj is string id
                ? id : throw new ArgumentException("Missing experimentId");
            var reason = message.Payload.TryGetValue("reason", out var reasonObj) && reasonObj is string r
                ? r : "Manual abort requested";

            await _engine.AbortExperimentAsync(experimentId, reason);

            message.Payload["success"] = true;
            message.Payload["experimentId"] = experimentId;
            message.Payload["abortReason"] = reason;
        }
        catch (Exception ex)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = ex.Message;
        }
    }

    private Task HandleScheduleAsync(PluginMessage message)
    {
        if (_vaccinationScheduler == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Vaccination scheduler not yet initialized (available in later plan)";
            return Task.CompletedTask;
        }

        message.Payload["success"] = true;
        return Task.CompletedTask;
    }

    private async Task HandleResultsAsync(PluginMessage message)
    {
        if (_resultsDatabase == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Results database not yet initialized (available in later plan)";
            return;
        }

        try
        {
            var query = new ExperimentQuery();
            var records = await _resultsDatabase.QueryAsync(query);

            message.Payload["success"] = true;
            message.Payload["count"] = records.Count;
        }
        catch (Exception ex)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = ex.Message;
        }
    }

    private async Task HandleImmuneMemoryAsync(PluginMessage message)
    {
        if (_immuneResponseSystem == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Immune response system not yet initialized (available in later plan)";
            return;
        }

        try
        {
            var entries = await _immuneResponseSystem.GetImmuneMemoryAsync();

            message.Payload["success"] = true;
            message.Payload["count"] = entries.Count;
        }
        catch (Exception ex)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = ex.Message;
        }
    }

    #endregion

    /// <summary>
    /// Releases resources held by this plugin.
    /// </summary>
    public new void Dispose()
    {
        if (_disposed)
            return;

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();

        _engine?.Dispose();

        _disposed = true;
    }
}
