using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.Engine.FaultInjectors;

/// <summary>
/// Fault injector that simulates network partitions between components.
/// Publishes partition start/end events to the message bus so that participating
/// plugins can simulate being isolated from each other.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public sealed class NetworkPartitionInjector : IFaultInjector
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, PartitionState> _activePartitions = new();

    /// <summary>
    /// Tracks the state of an active network partition.
    /// </summary>
    private sealed record PartitionState(
        string[] AffectedEndpoints,
        DateTimeOffset StartedAt);

    /// <inheritdoc/>
    public FaultType SupportedFaultType => FaultType.NetworkPartition;

    /// <inheritdoc/>
    public string Name => "Network Partition Injector";

    /// <summary>
    /// Initializes a new NetworkPartitionInjector with optional message bus for cluster coordination.
    /// </summary>
    /// <param name="messageBus">Optional message bus. Null for single-node mode.</param>
    public NetworkPartitionInjector(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public async Task<FaultInjectionResult> InjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        try
        {
            var targetPlugins = experiment.TargetPluginIds ?? Array.Empty<string>();
            var targetNodes = experiment.TargetNodeIds ?? Array.Empty<string>();
            var affectedEndpoints = targetPlugins.Concat(targetNodes).ToArray();

            if (affectedEndpoints.Length == 0)
            {
                affectedEndpoints = new[] { "default-partition-group" };
            }

            var state = new PartitionState(affectedEndpoints, DateTimeOffset.UtcNow);
            _activePartitions[experiment.Id] = state;

            if (_messageBus != null)
            {
                // Signal partition start to all participants
                await _messageBus.PublishAsync("chaos.fault.network-partition", new PluginMessage
                {
                    Type = "network.partition.start",
                    SourcePluginId = "com.datawarehouse.chaos.vaccination",
                    Payload = new Dictionary<string, object>
                    {
                        ["experimentId"] = experiment.Id,
                        ["affectedEndpoints"] = affectedEndpoints,
                        ["severity"] = experiment.Severity.ToString(),
                        ["action"] = "isolate"
                    }
                }, ct);
            }

            var signature = new FaultSignature
            {
                Hash = $"net-partition-{string.Join("-", affectedEndpoints.Take(3))}",
                FaultType = FaultType.NetworkPartition,
                Pattern = $"Network partition affecting {affectedEndpoints.Length} endpoints",
                AffectedComponents = affectedEndpoints,
                Severity = experiment.Severity,
                FirstObserved = DateTimeOffset.UtcNow,
                ObservationCount = 1
            };

            return new FaultInjectionResult
            {
                Success = true,
                FaultSignature = signature,
                AffectedComponents = affectedEndpoints,
                Metrics = new Dictionary<string, double>
                {
                    ["affectedEndpoints"] = affectedEndpoints.Length,
                    ["partitionStartEpochMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                },
                CleanupRequired = true
            };
        }
        catch (Exception ex)
        {
            return new FaultInjectionResult
            {
                Success = false,
                ErrorMessage = $"Failed to inject network partition: {ex.Message}",
                CleanupRequired = false
            };
        }
    }

    /// <inheritdoc/>
    public async Task CleanupAsync(string experimentId, CancellationToken ct)
    {
        if (!_activePartitions.TryRemove(experimentId, out var state))
            return;

        if (_messageBus != null)
        {
            await _messageBus.PublishAsync("chaos.fault.network-partition", new PluginMessage
            {
                Type = "network.partition.end",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["experimentId"] = experimentId,
                    ["affectedEndpoints"] = state.AffectedEndpoints,
                    ["action"] = "restore",
                    ["partitionDurationMs"] = (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public Task<bool> CanInjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        // Network partition can always be injected if we have a message bus or targets
        var hasTargets = (experiment.TargetPluginIds?.Length > 0) || (experiment.TargetNodeIds?.Length > 0);
        return Task.FromResult(hasTargets || _messageBus != null);
    }
}
