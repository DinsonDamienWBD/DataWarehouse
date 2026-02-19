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
/// Fault injector that simulates node crashes without actually killing processes.
/// Sends crash-simulation signals to target nodes via the message bus, instructing them
/// to stop responding to heartbeats and reject new requests.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public sealed class NodeCrashInjector : IFaultInjector
{
    private readonly IMessageBus? _messageBus;
    private readonly ConcurrentDictionary<string, NodeCrashState> _activeCrashes = new();

    /// <summary>
    /// Tracks the state of an active node crash simulation.
    /// </summary>
    private sealed record NodeCrashState(
        string[] CrashedNodeIds,
        DateTimeOffset StartedAt,
        int MissedHeartbeats);

    /// <inheritdoc/>
    public FaultType SupportedFaultType => FaultType.NodeCrash;

    /// <inheritdoc/>
    public string Name => "Node Crash Injector";

    /// <summary>
    /// Initializes a new NodeCrashInjector with optional message bus.
    /// </summary>
    /// <param name="messageBus">Optional message bus for cluster-wide coordination.</param>
    public NodeCrashInjector(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public async Task<FaultInjectionResult> InjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        try
        {
            var targetNodeIds = experiment.TargetNodeIds ?? Array.Empty<string>();
            if (targetNodeIds.Length == 0)
            {
                targetNodeIds = new[] { "node-local" };
            }

            var state = new NodeCrashState(targetNodeIds, DateTimeOffset.UtcNow, MissedHeartbeats: 0);
            _activeCrashes[experiment.Id] = state;

            if (_messageBus != null)
            {
                // Signal nodes to enter crash-simulation mode
                // They stop responding to heartbeats and reject new requests
                foreach (var nodeId in targetNodeIds)
                {
                    await _messageBus.PublishAsync("chaos.fault.node-crash", new PluginMessage
                    {
                        Type = "cluster.node.simulate-crash",
                        SourcePluginId = "com.datawarehouse.chaos.vaccination",
                        Payload = new Dictionary<string, object>
                        {
                            ["experimentId"] = experiment.Id,
                            ["targetNodeId"] = nodeId,
                            ["severity"] = experiment.Severity.ToString(),
                            ["action"] = "crash",
                            ["stopHeartbeats"] = true,
                            ["rejectRequests"] = true
                        }
                    }, ct);
                }
            }

            var signature = new FaultSignature
            {
                Hash = $"node-crash-{string.Join("-", targetNodeIds.Take(3))}",
                FaultType = FaultType.NodeCrash,
                Pattern = $"Node crash simulation affecting {targetNodeIds.Length} nodes",
                AffectedComponents = targetNodeIds,
                Severity = experiment.Severity,
                FirstObserved = DateTimeOffset.UtcNow,
                ObservationCount = 1
            };

            return new FaultInjectionResult
            {
                Success = true,
                FaultSignature = signature,
                AffectedComponents = targetNodeIds,
                Metrics = new Dictionary<string, double>
                {
                    ["crashedNodes"] = targetNodeIds.Length,
                    ["crashStartEpochMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["missedHeartbeats"] = 0
                },
                CleanupRequired = true
            };
        }
        catch (Exception ex)
        {
            return new FaultInjectionResult
            {
                Success = false,
                ErrorMessage = $"Failed to inject node crash: {ex.Message}",
                CleanupRequired = false
            };
        }
    }

    /// <inheritdoc/>
    public async Task CleanupAsync(string experimentId, CancellationToken ct)
    {
        if (!_activeCrashes.TryRemove(experimentId, out var state))
            return;

        if (_messageBus != null)
        {
            foreach (var nodeId in state.CrashedNodeIds)
            {
                await _messageBus.PublishAsync("chaos.fault.node-crash", new PluginMessage
                {
                    Type = "cluster.node.simulate-recover",
                    SourcePluginId = "com.datawarehouse.chaos.vaccination",
                    Payload = new Dictionary<string, object>
                    {
                        ["experimentId"] = experimentId,
                        ["targetNodeId"] = nodeId,
                        ["action"] = "recover",
                        ["crashDurationMs"] = (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds,
                        ["missedHeartbeats"] = state.MissedHeartbeats
                    }
                }, ct);
            }
        }
    }

    /// <inheritdoc/>
    public Task<bool> CanInjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        // Node crash simulation requires a message bus to communicate with target nodes
        // or explicit target node IDs
        var hasTargets = experiment.TargetNodeIds?.Length > 0;
        return Task.FromResult(hasTargets || _messageBus != null);
    }
}
