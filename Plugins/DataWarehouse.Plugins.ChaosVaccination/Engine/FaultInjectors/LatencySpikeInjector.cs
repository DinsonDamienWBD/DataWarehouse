using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.Engine.FaultInjectors;

/// <summary>
/// Fault injector that introduces artificial latency into operations.
/// Publishes latency spike events to the message bus so that participating plugins
/// can inject configurable delays into their processing paths.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public sealed class LatencySpikeInjector : IFaultInjector
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, LatencyState> _activeLatency = new();

    /// <summary>
    /// Tracks the state of an active latency spike injection.
    /// </summary>
    private sealed record LatencyState(
        int MinLatencyMs,
        int MaxLatencyMs,
        string AffectedOperations,
        DateTimeOffset StartedAt,
        int OperationsAffected);

    /// <inheritdoc/>
    public FaultType SupportedFaultType => FaultType.LatencySpike;

    /// <inheritdoc/>
    public string Name => "Latency Spike Injector";

    /// <summary>
    /// Initializes a new LatencySpikeInjector with optional message bus.
    /// </summary>
    /// <param name="messageBus">Optional message bus for cluster-wide coordination.</param>
    public LatencySpikeInjector(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public async Task<FaultInjectionResult> InjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        try
        {
            var minLatencyMs = ExtractIntParameter(experiment, "minLatencyMs", 100);
            var maxLatencyMs = ExtractIntParameter(experiment, "maxLatencyMs", 5000);
            var affectedOperations = ExtractStringParameter(experiment, "affectedOperations", "*");

            if (minLatencyMs > maxLatencyMs)
            {
                (minLatencyMs, maxLatencyMs) = (maxLatencyMs, minLatencyMs);
            }

            var state = new LatencyState(minLatencyMs, maxLatencyMs, affectedOperations, DateTimeOffset.UtcNow, 0);
            _activeLatency[experiment.Id] = state;

            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("chaos.fault.latency-spike", new PluginMessage
                {
                    Type = "chaos.fault.latency-spike",
                    SourcePluginId = "com.datawarehouse.chaos.vaccination",
                    Payload = new Dictionary<string, object>
                    {
                        ["experimentId"] = experiment.Id,
                        ["minLatencyMs"] = minLatencyMs,
                        ["maxLatencyMs"] = maxLatencyMs,
                        ["affectedOperations"] = affectedOperations,
                        ["severity"] = experiment.Severity.ToString(),
                        ["action"] = "inject"
                    }
                }, ct);
            }

            var averageLatency = (minLatencyMs + maxLatencyMs) / 2.0;

            var signature = new FaultSignature
            {
                Hash = $"latency-spike-{minLatencyMs}-{maxLatencyMs}-{affectedOperations}",
                FaultType = FaultType.LatencySpike,
                Pattern = $"Latency spike {minLatencyMs}-{maxLatencyMs}ms on {affectedOperations}",
                AffectedComponents = experiment.TargetPluginIds ?? Array.Empty<string>(),
                Severity = experiment.Severity,
                FirstObserved = DateTimeOffset.UtcNow,
                ObservationCount = 1
            };

            return new FaultInjectionResult
            {
                Success = true,
                FaultSignature = signature,
                AffectedComponents = experiment.TargetPluginIds ?? Array.Empty<string>(),
                Metrics = new Dictionary<string, double>
                {
                    ["averageInjectedLatencyMs"] = averageLatency,
                    ["maxInjectedLatencyMs"] = maxLatencyMs,
                    ["operationsAffected"] = 0
                },
                CleanupRequired = true
            };
        }
        catch (Exception ex)
        {
            return new FaultInjectionResult
            {
                Success = false,
                ErrorMessage = $"Failed to inject latency spike: {ex.Message}",
                CleanupRequired = false
            };
        }
    }

    /// <inheritdoc/>
    public async Task CleanupAsync(string experimentId, CancellationToken ct)
    {
        if (!_activeLatency.TryRemove(experimentId, out var state))
            return;

        if (_messageBus != null)
        {
            await _messageBus.PublishAsync("chaos.fault.latency-spike", new PluginMessage
            {
                Type = "chaos.fault.latency-clear",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["experimentId"] = experimentId,
                    ["action"] = "clear",
                    ["totalDurationMs"] = (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds,
                    ["operationsAffected"] = state.OperationsAffected
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public Task<bool> CanInjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        // Latency spike can always be injected -- it just publishes events
        return Task.FromResult(true);
    }

    private static int ExtractIntParameter(ChaosExperiment experiment, string key, int defaultValue)
    {
        if (experiment.Parameters.TryGetValue(key, out var value))
        {
            if (value is int i) return i;
            if (value is long l) return (int)l;
            if (value is double d) return (int)d;
            if (value is string s && int.TryParse(s, out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static string ExtractStringParameter(ChaosExperiment experiment, string key, string defaultValue)
    {
        if (experiment.Parameters.TryGetValue(key, out var value) && value is string s)
        {
            return s;
        }
        return defaultValue;
    }
}
