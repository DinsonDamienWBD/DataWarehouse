using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.Engine.FaultInjectors;

/// <summary>
/// Fault injector that simulates disk I/O failures. Publishes storage fault events
/// to the message bus so that storage plugins can simulate I/O errors (readonly, corrupt, slow, full).
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public sealed class DiskFailureInjector : IFaultInjector
{
    private readonly IMessageBus? _messageBus;
    private readonly BoundedDictionary<string, DiskFaultState> _activeFaults = new BoundedDictionary<string, DiskFaultState>(1000);

    /// <summary>
    /// Tracks the state of an active disk failure injection.
    /// </summary>
    private sealed record DiskFaultState(
        string[] AffectedPaths,
        string ErrorType,
        DateTimeOffset StartedAt,
        int IoErrorsGenerated);

    /// <inheritdoc/>
    public FaultType SupportedFaultType => FaultType.DiskFailure;

    /// <inheritdoc/>
    public string Name => "Disk Failure Injector";

    /// <summary>
    /// Initializes a new DiskFailureInjector with optional message bus.
    /// </summary>
    /// <param name="messageBus">Optional message bus for cluster-wide coordination.</param>
    public DiskFailureInjector(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public async Task<FaultInjectionResult> InjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        try
        {
            var affectedPaths = ExtractPaths(experiment);
            var errorType = ExtractErrorType(experiment);

            var state = new DiskFaultState(affectedPaths, errorType, DateTimeOffset.UtcNow, 0);
            _activeFaults[experiment.Id] = state;

            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("chaos.fault.disk-failure", new PluginMessage
                {
                    Type = "storage.fault.inject",
                    SourcePluginId = "com.datawarehouse.chaos.vaccination",
                    Payload = new Dictionary<string, object>
                    {
                        ["experimentId"] = experiment.Id,
                        ["affectedPaths"] = affectedPaths,
                        ["errorType"] = errorType,
                        ["severity"] = experiment.Severity.ToString(),
                        ["action"] = "inject"
                    }
                }, ct);
            }

            var signature = new FaultSignature
            {
                Hash = $"disk-failure-{errorType}-{affectedPaths.Length}",
                FaultType = FaultType.DiskFailure,
                Pattern = $"Disk {errorType} failure affecting {affectedPaths.Length} paths",
                AffectedComponents = affectedPaths,
                Severity = experiment.Severity,
                FirstObserved = DateTimeOffset.UtcNow,
                ObservationCount = 1
            };

            return new FaultInjectionResult
            {
                Success = true,
                FaultSignature = signature,
                AffectedComponents = affectedPaths,
                Metrics = new Dictionary<string, double>
                {
                    ["affectedPaths"] = affectedPaths.Length,
                    ["ioErrorsGenerated"] = 0
                },
                CleanupRequired = true
            };
        }
        catch (Exception ex)
        {
            return new FaultInjectionResult
            {
                Success = false,
                ErrorMessage = $"Failed to inject disk failure: {ex.Message}",
                CleanupRequired = false
            };
        }
    }

    /// <inheritdoc/>
    public async Task CleanupAsync(string experimentId, CancellationToken ct)
    {
        if (!_activeFaults.TryRemove(experimentId, out var state))
            return;

        if (_messageBus != null)
        {
            await _messageBus.PublishAsync("chaos.fault.disk-failure", new PluginMessage
            {
                Type = "storage.fault.clear",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["experimentId"] = experimentId,
                    ["affectedPaths"] = state.AffectedPaths,
                    ["action"] = "clear"
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public Task<bool> CanInjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        // Disk failure injection requires a message bus or explicit target paths
        var hasPaths = experiment.Parameters.ContainsKey("targetPaths");
        return Task.FromResult(hasPaths || _messageBus != null);
    }

    private static string[] ExtractPaths(ChaosExperiment experiment)
    {
        if (experiment.Parameters.TryGetValue("targetPaths", out var pathsObj))
        {
            if (pathsObj is string[] paths) return paths;
            if (pathsObj is string singlePath) return new[] { singlePath };
        }

        // Default paths for simulation
        return new[] { "/data/primary", "/data/replica" };
    }

    private static string ExtractErrorType(ChaosExperiment experiment)
    {
        if (experiment.Parameters.TryGetValue("errorType", out var etObj) && etObj is string errorType)
        {
            return errorType;
        }

        return experiment.Severity switch
        {
            FaultSeverity.Low => "slow",
            FaultSeverity.Medium => "readonly",
            FaultSeverity.High => "corrupt",
            FaultSeverity.Critical or FaultSeverity.Catastrophic => "full",
            _ => "readonly"
        };
    }
}
