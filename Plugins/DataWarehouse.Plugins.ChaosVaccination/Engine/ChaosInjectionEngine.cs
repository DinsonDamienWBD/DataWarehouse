using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.ChaosVaccination.Engine.FaultInjectors;

namespace DataWarehouse.Plugins.ChaosVaccination.Engine;

/// <summary>
/// Core chaos injection engine that orchestrates experiment lifecycle:
/// validate safety -> select injector -> inject fault -> observe -> collect results -> cleanup.
///
/// Features:
/// - Auto-discovers fault injectors via reflection (strategy pattern)
/// - Thread-safe concurrent experiment management with configurable limits
/// - Safety validation before every experiment execution
/// - Mid-flight experiment abort with cleanup
/// - Message bus integration for fault coordination across the cluster
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public sealed class ChaosInjectionEngine : IChaosInjectionEngine, IDisposable
{
    private readonly IMessageBus? _messageBus;
    private readonly ChaosVaccinationOptions _options;
    private readonly BoundedDictionary<string, RunningExperiment> _runningExperiments = new BoundedDictionary<string, RunningExperiment>(1000);
    private readonly Dictionary<FaultType, IFaultInjector> _injectors = new();
    private readonly SemaphoreSlim _concurrencyLimiter;
    private bool _disposed;

    /// <summary>
    /// Tracks a running experiment with its associated state.
    /// </summary>
    private sealed record RunningExperiment(
        ChaosExperiment Experiment,
        CancellationTokenSource Cts,
        DateTimeOffset StartedAt,
        IFaultInjector Injector);

    /// <inheritdoc/>
    public event Action<ChaosExperimentEvent>? OnExperimentEvent;

    /// <summary>
    /// Initializes a new ChaosInjectionEngine with optional message bus and configuration.
    /// </summary>
    /// <param name="messageBus">Optional message bus for cluster-wide fault coordination. Null for single-node mode.</param>
    /// <param name="options">Configuration options for the chaos vaccination system.</param>
    public ChaosInjectionEngine(IMessageBus? messageBus, ChaosVaccinationOptions options)
    {
        _messageBus = messageBus;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _concurrencyLimiter = new SemaphoreSlim(options.MaxConcurrentExperiments, options.MaxConcurrentExperiments);

        DiscoverInjectors();
    }

    /// <inheritdoc/>
    public async Task<ChaosExperimentResult> ExecuteExperimentAsync(ChaosExperiment experiment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);

        // Validate safety before execution
        var isSafe = await ValidateExperimentSafetyAsync(experiment, ct);
        if (!isSafe)
        {
            return new ChaosExperimentResult
            {
                ExperimentId = experiment.Id,
                Status = ExperimentStatus.Failed,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                ActualBlastRadius = BlastRadiusLevel.SingleStrategy,
                Observations = new[] { "Safety validation failed -- experiment not executed" }
            };
        }

        // Select the appropriate injector
        if (!_injectors.TryGetValue(experiment.FaultType, out var injector))
        {
            return new ChaosExperimentResult
            {
                ExperimentId = experiment.Id,
                Status = ExperimentStatus.Failed,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                ActualBlastRadius = BlastRadiusLevel.SingleStrategy,
                Observations = new[] { $"No injector registered for fault type: {experiment.FaultType}" }
            };
        }

        // Check if injector can inject
        var canInject = await injector.CanInjectAsync(experiment, ct);
        if (!canInject)
        {
            return new ChaosExperimentResult
            {
                ExperimentId = experiment.Id,
                Status = ExperimentStatus.Failed,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                ActualBlastRadius = BlastRadiusLevel.SingleStrategy,
                Observations = new[] { $"Injector pre-check failed for {injector.Name}" }
            };
        }

        // Enforce concurrent experiment limit
        if (!await _concurrencyLimiter.WaitAsync(TimeSpan.Zero, ct))
        {
            return new ChaosExperimentResult
            {
                ExperimentId = experiment.Id,
                Status = ExperimentStatus.Failed,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
                ActualBlastRadius = BlastRadiusLevel.SingleStrategy,
                Observations = new[] { $"Maximum concurrent experiments ({_options.MaxConcurrentExperiments}) reached" }
            };
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var startedAt = DateTimeOffset.UtcNow;
        var running = new RunningExperiment(experiment, cts, startedAt, injector);

        if (!_runningExperiments.TryAdd(experiment.Id, running))
        {
            _concurrencyLimiter.Release();
            cts.Dispose();
            return new ChaosExperimentResult
            {
                ExperimentId = experiment.Id,
                Status = ExperimentStatus.Failed,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                ActualBlastRadius = BlastRadiusLevel.SingleStrategy,
                Observations = new[] { $"Experiment {experiment.Id} is already running" }
            };
        }

        try
        {
            // Fire Started event
            FireEvent(experiment.Id, ChaosExperimentEventType.Started);

            // Inject the fault
            var injectionResult = await injector.InjectAsync(experiment, cts.Token);

            if (!injectionResult.Success)
            {
                return BuildResult(experiment.Id, ExperimentStatus.Failed, startedAt,
                    BlastRadiusLevel.SingleStrategy, injectionResult,
                    new[] { $"Fault injection failed: {injectionResult.ErrorMessage ?? "unknown error"}" });
            }

            // Fire FaultInjected event
            FireEvent(experiment.Id, ChaosExperimentEventType.FaultInjected);

            // Wait for experiment duration (or until aborted)
            try
            {
                await Task.Delay(experiment.DurationLimit, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Experiment was aborted -- cleanup handled in AbortExperimentAsync
                return BuildResult(experiment.Id, ExperimentStatus.Aborted, startedAt,
                    BlastRadiusLevel.SingleStrategy, injectionResult,
                    new[] { "Experiment aborted during observation period" });
            }

            // Cleanup the injected fault
            if (injectionResult.CleanupRequired)
            {
                await injector.CleanupAsync(experiment.Id, ct);
            }

            // Build final result
            var result = BuildResult(experiment.Id, ExperimentStatus.Completed, startedAt,
                experiment.MaxBlastRadius, injectionResult,
                new[] { $"Experiment completed successfully after {experiment.DurationLimit.TotalSeconds}s" });

            // Fire Completed event
            FireEvent(experiment.Id, ChaosExperimentEventType.Completed);

            // Publish result to message bus
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("chaos.experiment.completed", new PluginMessage
                {
                    Type = "chaos.experiment.completed",
                    SourcePluginId = "com.datawarehouse.chaos.vaccination",
                    Payload = new Dictionary<string, object>
                    {
                        ["experimentId"] = result.ExperimentId,
                        ["status"] = result.Status.ToString(),
                        ["faultType"] = experiment.FaultType.ToString(),
                        ["durationMs"] = (result.CompletedAt!.Value - result.StartedAt).TotalMilliseconds
                    }
                }, ct);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            FireEvent(experiment.Id, ChaosExperimentEventType.Aborted, "Operation cancelled");
            return BuildResult(experiment.Id, ExperimentStatus.Aborted, startedAt,
                BlastRadiusLevel.SingleStrategy, null, new[] { "Experiment cancelled" });
        }
        catch (Exception ex)
        {
            FireEvent(experiment.Id, ChaosExperimentEventType.Completed, $"Failed: {ex.Message}");
            return BuildResult(experiment.Id, ExperimentStatus.Failed, startedAt,
                BlastRadiusLevel.SingleStrategy, null, new[] { $"Experiment failed: {ex.Message}" });
        }
        finally
        {
            _runningExperiments.TryRemove(experiment.Id, out _);
            _concurrencyLimiter.Release();
        }
    }

    /// <inheritdoc/>
    public async Task AbortExperimentAsync(string experimentId, string reason, CancellationToken ct = default)
    {
        if (!_runningExperiments.TryGetValue(experimentId, out var running))
        {
            throw new InvalidOperationException($"Experiment '{experimentId}' is not currently running");
        }

        // Cancel the experiment
        await running.Cts.CancelAsync();

        // Cleanup the injected fault
        try
        {
            await running.Injector.CleanupAsync(experimentId, ct);
        }
        catch (Exception)
        {
            // Cleanup errors should not prevent abort from completing
        }

        FireEvent(experimentId, ChaosExperimentEventType.Aborted, reason);

        // Publish abort to message bus
        if (_messageBus != null)
        {
            await _messageBus.PublishAsync("chaos.experiment.aborted", new PluginMessage
            {
                Type = "chaos.experiment.aborted",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["experimentId"] = experimentId,
                    ["reason"] = reason
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ChaosExperimentResult>> GetRunningExperimentsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var results = _runningExperiments.Values.Select(r => new ChaosExperimentResult
        {
            ExperimentId = r.Experiment.Id,
            Status = ExperimentStatus.Running,
            StartedAt = r.StartedAt,
            ActualBlastRadius = r.Experiment.MaxBlastRadius,
            AffectedPlugins = r.Experiment.TargetPluginIds ?? Array.Empty<string>(),
            AffectedNodes = r.Experiment.TargetNodeIds ?? Array.Empty<string>()
        }).ToList();

        return Task.FromResult<IReadOnlyList<ChaosExperimentResult>>(results);
    }

    /// <inheritdoc/>
    public async Task<bool> ValidateExperimentSafetyAsync(ChaosExperiment experiment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);

        // Check blast radius against global limit
        if (experiment.MaxBlastRadius > _options.GlobalBlastRadiusLimit)
        {
            FireEvent(experiment.Id, ChaosExperimentEventType.SafetyCheckFailed,
                $"Requested blast radius {experiment.MaxBlastRadius} exceeds global limit {_options.GlobalBlastRadiusLimit}");
            return false;
        }

        // Check severity constraints in safe mode
        if (_options.SafeMode && experiment.Severity >= FaultSeverity.Critical)
        {
            if (_options.RequireApprovalForCritical)
            {
                FireEvent(experiment.Id, ChaosExperimentEventType.SafetyCheckFailed,
                    $"Critical severity experiment requires approval in safe mode");
                return false;
            }
        }

        // Run all safety checks defined on the experiment
        foreach (var check in experiment.SafetyChecks)
        {
            try
            {
                var passed = await check.CheckAction(ct);
                if (!passed && check.IsRequired)
                {
                    FireEvent(experiment.Id, ChaosExperimentEventType.SafetyCheckFailed,
                        $"Required safety check failed: {check.Name}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (check.IsRequired)
                {
                    FireEvent(experiment.Id, ChaosExperimentEventType.SafetyCheckFailed,
                        $"Safety check '{check.Name}' threw: {ex.Message}");
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the registered fault injectors.
    /// </summary>
    public IReadOnlyDictionary<FaultType, IFaultInjector> Injectors => _injectors;

    /// <summary>
    /// Discovers and registers all <see cref="IFaultInjector"/> implementations
    /// in the current assembly via reflection.
    /// </summary>
    private void DiscoverInjectors()
    {
        var injectorType = typeof(IFaultInjector);
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !injectorType.IsAssignableFrom(type))
                continue;

            // Injectors accept optional IMessageBus in constructor
            IFaultInjector? injector = null;

            var ctorWithBus = type.GetConstructor(new[] { typeof(IMessageBus) });
            if (ctorWithBus != null)
            {
                injector = (IFaultInjector)ctorWithBus.Invoke(new object?[] { _messageBus });
            }
            else
            {
                var defaultCtor = type.GetConstructor(Type.EmptyTypes);
                if (defaultCtor != null)
                {
                    injector = (IFaultInjector)defaultCtor.Invoke(null);
                }
            }

            if (injector != null)
            {
                _injectors[injector.SupportedFaultType] = injector;
            }
        }
    }

    private ChaosExperimentResult BuildResult(
        string experimentId,
        ExperimentStatus status,
        DateTimeOffset startedAt,
        BlastRadiusLevel blastRadius,
        FaultInjectionResult? injectionResult,
        string[] observations)
    {
        return new ChaosExperimentResult
        {
            ExperimentId = experimentId,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            ActualBlastRadius = blastRadius,
            FaultSignature = injectionResult?.FaultSignature,
            AffectedPlugins = injectionResult?.AffectedComponents ?? Array.Empty<string>(),
            Metrics = injectionResult?.Metrics != null
                ? new Dictionary<string, double>(injectionResult.Metrics)
                : new Dictionary<string, double>(),
            Observations = observations,
            RecoveryTimeMs = status == ExperimentStatus.Completed
                ? (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds
                : null
        };
    }

    private void FireEvent(string experimentId, ChaosExperimentEventType eventType, string? detail = null)
    {
        OnExperimentEvent?.Invoke(new ChaosExperimentEvent
        {
            ExperimentId = experimentId,
            EventType = eventType,
            Timestamp = DateTimeOffset.UtcNow,
            Detail = detail
        });
    }

    /// <summary>
    /// Releases resources held by the engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        // Abort all running experiments
        foreach (var kvp in _runningExperiments)
        {
            try
            {
                kvp.Value.Cts.Cancel();
                kvp.Value.Cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
        }
        _runningExperiments.Clear();

        _concurrencyLimiter.Dispose();
        _disposed = true;
    }
}
