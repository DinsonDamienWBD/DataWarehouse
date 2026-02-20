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
using DataWarehouse.Plugins.ChaosVaccination.BlastRadius;
using DataWarehouse.Plugins.ChaosVaccination.Engine;
using DataWarehouse.Plugins.ChaosVaccination.ImmuneResponse;
using DataWarehouse.Plugins.ChaosVaccination.Integration;
using DataWarehouse.Plugins.ChaosVaccination.Scheduling;
using DataWarehouse.Plugins.ChaosVaccination.Storage;

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
///
/// Sub-component wiring (dependency order):
/// 1. FaultSignatureAnalyzer (stateless)
/// 2. RemediationExecutor (needs bus)
/// 3. ImmuneResponseSystem (needs bus, analyzer)
/// 4. InMemoryChaosResultsDatabase (needs bus)
/// 5. ChaosInjectionEngine (needs bus, options)
/// 6. IsolationZoneManager (needs bus)
/// 7. FailurePropagationMonitor (needs zone manager, bus)
/// 8. BlastRadiusEnforcer (needs bus, zone manager, propagation monitor)
/// 9. VaccinationScheduler (needs engine, bus)
/// 10. ExistingResilienceIntegration (needs bus) -- post-intelligence
/// 11. ChaosVaccinationMessageHandler (needs all above) -- post-intelligence
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination orchestrator")]
public sealed class ChaosVaccinationPlugin : ResiliencePluginBase, IDisposable
{
    // Sub-component fields (initialized in dependency order during startup)
    private ChaosInjectionEngine? _engine;
    private IsolationZoneManager? _zoneManager;
    private FailurePropagationMonitor? _propagationMonitor;
    private BlastRadiusEnforcer? _enforcer;
    private ImmuneResponseSystem? _immuneSystem;
    private FaultSignatureAnalyzer? _signatureAnalyzer;
    private RemediationExecutor? _remediationExecutor;
    private VaccinationScheduler? _scheduler;
    private InMemoryChaosResultsDatabase? _resultsDb;
    private ExistingResilienceIntegration? _resilienceIntegration;
    private ChaosVaccinationMessageHandler? _messageHandler;
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

    /// <summary>
    /// Gets the blast radius enforcer instance, if initialized.
    /// </summary>
    public IBlastRadiusEnforcer? BlastRadiusEnforcer => _enforcer;

    /// <summary>
    /// Gets the immune response system instance, if initialized.
    /// </summary>
    public IImmuneResponseSystem? ImmuneSystem => _immuneSystem;

    /// <summary>
    /// Gets the vaccination scheduler instance, if initialized.
    /// </summary>
    public IVaccinationScheduler? Scheduler => _scheduler;

    /// <summary>
    /// Gets the results database instance, if initialized.
    /// </summary>
    public IChaosResultsDatabase? ResultsDatabase => _resultsDb;

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
            var capabilities = new List<RegisteredCapability>
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
                },
                new()
                {
                    CapabilityId = $"{Id}.blast-radius",
                    DisplayName = $"{Name} - Blast Radius Enforcement",
                    Description = "Create and manage isolation zones for blast radius enforcement during chaos experiments",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "chaos", "blast-radius", "isolation", "safety" }
                }
            };

            // Per-fault-type capabilities
            foreach (var faultType in new[] { "NetworkPartition", "DiskFailure", "NodeCrash", "LatencySpike", "MemoryPressure" })
            {
                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.fault.{faultType.ToLowerInvariant()}",
                    DisplayName = $"{Name} - {faultType} Injection",
                    Description = $"Inject {faultType} faults for chaos vaccination",
                    Category = DataWarehouse.SDK.Contracts.CapabilityCategory.Resilience,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "chaos", "fault-injection", faultType.ToLowerInvariant() }
                });
            }

            return capabilities;
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        // System overview
        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.overview",
            Topic = "chaos.vaccination",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = "Chaos vaccination system with automated fault injection, immune response learning, blast radius enforcement, and vaccination scheduling",
            Payload = new Dictionary<string, object>
            {
                ["faultTypes"] = new[] { "NetworkPartition", "DiskFailure", "NodeCrash", "LatencySpike", "MemoryPressure" },
                ["safetyFirst"] = true,
                ["supportsAbort"] = true,
                ["supportsScheduling"] = true,
                ["supportsImmuneMemory"] = true,
                ["supportsBlastRadiusEnforcement"] = true,
                // Note: static knowledge reports capability presence, not runtime counts.
                // Runtime metrics are available via GetResilienceHealthAsync.
                ["immuneMemorySize"] = _immuneSystem != null ? -1 : 0,
                ["activeSchedules"] = _scheduler != null ? -1 : 0
            },
            Tags = new[] { "chaos", "vaccination", "fault-injection", "resilience" }
        });

        // Per-fault-type knowledge
        foreach (var faultType in new[] { "NetworkPartition", "DiskFailure", "NodeCrash", "LatencySpike", "MemoryPressure" })
        {
            knowledge.Add(new KnowledgeObject
            {
                Id = $"{Id}.fault.{faultType.ToLowerInvariant()}",
                Topic = $"chaos.vaccination.fault.{faultType.ToLowerInvariant()}",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Chaos vaccination fault injector for {faultType} scenarios",
                Payload = new Dictionary<string, object>
                {
                    ["faultType"] = faultType,
                    ["injectorAvailable"] = _engine?.Injectors.ContainsKey(Enum.Parse<FaultType>(faultType)) ?? false
                },
                Tags = new[] { "chaos", "fault-injection", faultType.ToLowerInvariant() }
            });
        }

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
        response.Metadata["EngineInitialized"] = (_engine != null).ToString();
        response.Metadata["InjectorCount"] = (_engine?.Injectors.Count ?? 0).ToString();

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
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        // Initialize sub-components in dependency order (Phase 1: core components)
        _options = new ChaosVaccinationOptions();

        // 1. Stateless analyzers
        _signatureAnalyzer = new FaultSignatureAnalyzer();

        // 2. Remediation executor (needs bus)
        _remediationExecutor = new RemediationExecutor(MessageBus);

        // 3. Immune response system (needs bus, analyzer)
        _immuneSystem = new ImmuneResponseSystem(MessageBus, _signatureAnalyzer);

        // 4. Results database (needs bus)
        _resultsDb = new InMemoryChaosResultsDatabase(MessageBus);

        // 5. Chaos injection engine (needs bus, options)
        _engine = new ChaosInjectionEngine(MessageBus, _options);

        // 6. Isolation zone manager (needs bus)
        _zoneManager = new IsolationZoneManager(MessageBus);

        // 7. Failure propagation monitor (needs zone manager, bus)
        _propagationMonitor = new FailurePropagationMonitor(_zoneManager, MessageBus);

        // 8. Blast radius enforcer (needs bus, zone manager, propagation monitor)
        _enforcer = new BlastRadiusEnforcer(MessageBus, _zoneManager, _propagationMonitor, _options);

        // 9. Vaccination scheduler (needs engine, bus)
        _scheduler = new VaccinationScheduler(_engine, MessageBus);

        // Register basic bus subscriptions
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
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        // 10. Resilience integration bridge (needs bus) -- requires intelligence phase
        if (MessageBus != null)
        {
            _resilienceIntegration = new ExistingResilienceIntegration(MessageBus);
        }

        // 11. Message handler (needs all sub-components) -- routes all chaos.* topics
        if (_engine != null && _enforcer != null && _immuneSystem != null && _scheduler != null && _resultsDb != null)
        {
            _messageHandler = new ChaosVaccinationMessageHandler(
                _engine, _enforcer, _immuneSystem, _scheduler, _resultsDb);

            if (MessageBus != null)
            {
                _messageHandler.RegisterSubscriptions(MessageBus);
            }
        }

        // Wire engine events: store results and feed to immune system for learning
        if (_engine != null)
        {
            _engine.OnExperimentEvent += async (evt) =>
            {
                if (evt.EventType == ChaosExperimentEventType.Completed && _resultsDb != null)
                {
                    try
                    {
                        // Get the running experiments to find the completed one's result
                        // The event fires during execution, so we create a record from the event
                        var record = new ExperimentRecord
                        {
                            Result = new ChaosExperimentResult
                            {
                                ExperimentId = evt.ExperimentId,
                                Status = ExperimentStatus.Completed,
                                StartedAt = evt.Timestamp.AddSeconds(-30), // Approximate
                                CompletedAt = evt.Timestamp,
                                ActualBlastRadius = BlastRadiusLevel.SinglePlugin
                            },
                            TriggeredBy = "engine-event",
                            CreatedAt = DateTimeOffset.UtcNow
                        };

                        await _resultsDb.StoreAsync(record, CancellationToken.None);

                        // Feed to immune system for learning
                        if (_immuneSystem != null)
                        {
                            await _immuneSystem.LearnFromExperimentAsync(
                                record.Result,
                                Array.Empty<RemediationAction>(),
                                CancellationToken.None);
                        }
                    }
                    catch
                    {
                        // Event handlers must not crash the plugin
                    }
                }
            };
        }

        // Wire enforcer breach events: publish alert and check immune memory for remediation
        if (_enforcer != null)
        {
            _enforcer.OnBreachDetected += async (breachEvt) =>
            {
                try
                {
                    // Publish alert
                    if (MessageBus != null)
                    {
                        await MessageBus.PublishAsync("chaos.blast-radius.breach.alert", new PluginMessage
                        {
                            Type = "chaos.blast-radius.breach.alert",
                            SourcePluginId = Id,
                            Payload = new Dictionary<string, object>
                            {
                                ["zoneId"] = breachEvt.ZoneId,
                                ["actualRadius"] = breachEvt.ActualRadius.ToString(),
                                ["maxLevel"] = breachEvt.Policy.MaxLevel.ToString(),
                                ["timestamp"] = breachEvt.Timestamp.ToString("O")
                            }
                        }, CancellationToken.None);
                    }

                    // Check immune memory for a known remediation
                    if (_immuneSystem != null && _signatureAnalyzer != null)
                    {
                        var signature = _signatureAnalyzer.GenerateSignatureFromEvent(
                            breachEvt.ZoneId,
                            "local",
                            FaultType.Custom,
                            $"blast-radius-breach:{breachEvt.ActualRadius}");

                        var memoryEntry = await _immuneSystem.RecognizeFaultAsync(signature, CancellationToken.None);
                        if (memoryEntry != null)
                        {
                            await _immuneSystem.ApplyRemediationAsync(memoryEntry, CancellationToken.None);
                        }
                    }
                }
                catch
                {
                    // Breach handlers must not crash the plugin
                }
            };
        }

        // Register capabilities and knowledge
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
                    ["capabilities"] = GetCapabilities().Select(c => c.Name).ToArray(),
                    ["injectorCount"] = _engine?.Injectors.Count ?? 0,
                    ["immuneMemorySize"] = _immuneSystem != null
                        ? (await _immuneSystem.GetImmuneMemoryAsync(ct)).Count
                        : 0
                }
            }, ct);
        }
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
    public override async Task<ResilienceHealthInfo> GetResilienceHealthAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var runningExperiments = _engine != null
            ? await _engine.GetRunningExperimentsAsync(ct).ConfigureAwait(false)
            : (IReadOnlyList<ChaosExperimentResult>)Array.Empty<ChaosExperimentResult>();

        var immuneMemory = _immuneSystem != null
            ? await _immuneSystem.GetImmuneMemoryAsync(ct).ConfigureAwait(false)
            : (IReadOnlyList<ImmuneMemoryEntry>)Array.Empty<ImmuneMemoryEntry>();
        var immuneMemorySize = immuneMemory.Count;

        var activeZones = _enforcer != null
            ? await _enforcer.GetActiveZonesAsync(ct).ConfigureAwait(false)
            : (IReadOnlyList<IsolationZone>)Array.Empty<IsolationZone>();

        var schedules = _scheduler != null
            ? await _scheduler.GetSchedulesAsync(ct).ConfigureAwait(false)
            : (IReadOnlyList<VaccinationSchedule>)Array.Empty<VaccinationSchedule>();
        var enabledSchedules = schedules.Count(s => s.Enabled);

        var policyStates = new Dictionary<string, string>
        {
            ["ChaosEnabled"] = _options.Enabled ? "Active" : "Disabled",
            ["EngineStatus"] = _engine != null ? "Initialized" : "NotInitialized",
            ["ImmuneMemoryEntries"] = immuneMemorySize.ToString(),
            ["ActiveIsolationZones"] = activeZones.Count.ToString(),
            ["EnabledSchedules"] = enabledSchedules.ToString(),
            ["TotalSchedules"] = schedules.Count.ToString(),
            ["ResultsDbCount"] = (_resultsDb?.Count ?? 0).ToString()
        };

        foreach (var experiment in runningExperiments)
        {
            policyStates[$"Experiment:{experiment.ExperimentId}"] = experiment.Status.ToString();
        }

        foreach (var zone in activeZones)
        {
            policyStates[$"Zone:{zone.ZoneId}"] = zone.IsActive ? "Active" : "Inactive";
        }

        return new ResilienceHealthInfo(
            TotalPolicies: runningExperiments.Count + activeZones.Count + enabledSchedules + 1,
            ActiveCircuitBreakers: activeZones.Count,
            PolicyStates: policyStates);
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["ChaosEnabled"] = _options.Enabled;
        metadata["MaxConcurrentExperiments"] = _options.MaxConcurrentExperiments;
        metadata["GlobalBlastRadiusLimit"] = _options.GlobalBlastRadiusLimit.ToString();
        metadata["SafeMode"] = _options.SafeMode;
        metadata["InjectorCount"] = _engine?.Injectors.Count ?? 0;
        // GetMetadata is a synchronous contract (PluginBase.GetMetadata). Use Task.Run to avoid
        // deadlocks on synchronization-context-bound threads when awaiting the async calls.
        metadata["ImmuneMemorySize"] = _immuneSystem != null
            ? Task.Run(() => _immuneSystem.GetImmuneMemoryAsync()).ConfigureAwait(false).GetAwaiter().GetResult().Count
            : 0;
        metadata["ActiveZones"] = _enforcer != null
            ? Task.Run(() => _enforcer.GetActiveZonesAsync()).ConfigureAwait(false).GetAwaiter().GetResult().Count
            : 0;
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

            // Store result in database
            if (_resultsDb != null)
            {
                await _resultsDb.StoreAsync(new ExperimentRecord
                {
                    Result = result,
                    TriggeredBy = message.SourcePluginId ?? "direct-message",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

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

    private async Task HandleScheduleAsync(PluginMessage message)
    {
        if (_scheduler == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Vaccination scheduler not initialized";
            return;
        }

        try
        {
            var schedules = await _scheduler.GetSchedulesAsync();
            message.Payload["success"] = true;
            message.Payload["count"] = schedules.Count;
        }
        catch (Exception ex)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = ex.Message;
        }
    }

    private async Task HandleResultsAsync(PluginMessage message)
    {
        if (_resultsDb == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Results database not initialized";
            return;
        }

        try
        {
            var query = new ExperimentQuery();
            var records = await _resultsDb.QueryAsync(query);

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
        if (_immuneSystem == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Immune response system not initialized";
            return;
        }

        try
        {
            var entries = await _immuneSystem.GetImmuneMemoryAsync();

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
    /// Releases resources held by this plugin, including all sub-components.
    /// </summary>
    public new void Dispose()
    {
        if (_disposed)
            return;

        // Unregister message handler subscriptions
        _messageHandler?.UnregisterSubscriptions();

        // Dispose bus subscriptions
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }
        _subscriptions.Clear();

        // Dispose sub-components in reverse dependency order
        _resilienceIntegration?.Dispose();
        _scheduler?.Dispose();
        _enforcer?.Dispose();
        _propagationMonitor?.Dispose();
        _zoneManager?.Dispose();
        _engine?.Dispose();

        _disposed = true;
    }
}
