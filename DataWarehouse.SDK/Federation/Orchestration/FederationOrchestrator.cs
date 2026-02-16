using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Distributed;
using DataWarehouse.SDK.Federation.Topology;
using DataWarehouse.SDK.Utilities;
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Federation.Orchestration;

/// <summary>
/// Federation orchestrator with Raft-backed topology management and health monitoring.
/// </summary>
/// <remarks>
/// <para>
/// FederationOrchestrator manages the lifecycle of a multi-node storage cluster. It integrates
/// with SWIM cluster membership for failure detection and Raft consensus for topology consistency.
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// </para>
/// <list type="bullet">
///   <item><description>Node registration: Adds new nodes to the cluster topology via Raft</description></item>
///   <item><description>Health monitoring: Periodic heartbeat checks with automatic health degradation</description></item>
///   <item><description>Topology consistency: Raft ensures all nodes see the same topology</description></item>
///   <item><description>Failure handling: Syncs SWIM membership events to topology state</description></item>
/// </list>
/// <para>
/// <strong>Raft Integration:</strong> Topology changes (add/remove node) are proposed via Raft
/// to prevent split-brain. If Raft is unavailable, topology updates are local-only (for
/// single-node or non-HA deployments).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Federation orchestrator with Raft-backed topology")]
public sealed class FederationOrchestrator : IFederationOrchestrator, ITopologyProvider
{
    private readonly IClusterMembership _membership;
    private readonly IConsensusEngine? _raft;
    private readonly IMessageBus _messageBus;
    private readonly ClusterTopology _topology;
    private readonly PeriodicTimer _healthCheckTimer;
    private readonly CancellationTokenSource _cts;
    private readonly FederationOrchestratorConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="FederationOrchestrator"/> class.
    /// </summary>
    /// <param name="membership">The cluster membership service (SWIM).</param>
    /// <param name="messageBus">The message bus for event publishing.</param>
    /// <param name="raft">The Raft consensus engine (optional; null for single-node deployments).</param>
    /// <param name="config">Configuration options (optional; defaults to 10-second health checks).</param>
    public FederationOrchestrator(
        IClusterMembership membership,
        IMessageBus messageBus,
        IConsensusEngine? raft = null,
        FederationOrchestratorConfiguration? config = null)
    {
        _membership = membership;
        _raft = raft;
        _messageBus = messageBus;
        _topology = new ClusterTopology();
        _config = config ?? new FederationOrchestratorConfiguration();
        _healthCheckTimer = new PeriodicTimer(TimeSpan.FromSeconds(_config.HealthCheckIntervalSeconds));
        _cts = new CancellationTokenSource();
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        // Subscribe to membership changes
        _membership.OnMembershipChanged += HandleMembershipChanged;

        // Start health check loop
        _ = RunHealthCheckLoopAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        _cts.Cancel();
        _membership.OnMembershipChanged -= HandleMembershipChanged;
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RegisterNodeAsync(NodeRegistration registration, CancellationToken ct = default)
    {
        var nodeTopology = new NodeTopology
        {
            NodeId = registration.NodeId,
            Address = registration.Address,
            Port = registration.Port,
            Rack = registration.Rack,
            Datacenter = registration.Datacenter,
            Region = registration.Region,
            Latitude = registration.Latitude,
            Longitude = registration.Longitude,
            TotalBytes = registration.TotalBytes,
            FreeBytes = registration.TotalBytes,
            HealthScore = 1.0,
            LastHeartbeat = DateTimeOffset.UtcNow
        };

        // Propose topology change via Raft if available
        if (_raft != null)
        {
            var command = new { action = "add-node", node = nodeTopology };
            var proposal = new Proposal
            {
                Command = "topology-update",
                Payload = JsonSerializer.SerializeToUtf8Bytes(command)
            };
            await _raft.ProposeAsync(proposal).ConfigureAwait(false);
        }
        else
        {
            _topology.AddOrUpdateNode(nodeTopology);
        }

        // Publish event
        var message = new PluginMessage
        {
            Type = "federation.node.registered",
            Payload = new Dictionary<string, object>
            {
                ["nodeId"] = nodeTopology.NodeId,
                ["address"] = nodeTopology.Address,
                ["port"] = nodeTopology.Port,
                ["topology"] = nodeTopology
            }
        };
        await _messageBus.PublishAsync("federation.node.registered", message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UnregisterNodeAsync(string nodeId, CancellationToken ct = default)
    {
        if (_raft != null)
        {
            var command = new { action = "remove-node", nodeId };
            var proposal = new Proposal
            {
                Command = "topology-update",
                Payload = JsonSerializer.SerializeToUtf8Bytes(command)
            };
            await _raft.ProposeAsync(proposal).ConfigureAwait(false);
        }
        else
        {
            _topology.RemoveNode(nodeId);
        }

        var message = new PluginMessage
        {
            Type = "federation.node.unregistered",
            Payload = new Dictionary<string, object>
            {
                ["nodeId"] = nodeId
            }
        };
        await _messageBus.PublishAsync("federation.node.unregistered", message, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendHeartbeatAsync(NodeHeartbeat heartbeat, CancellationToken ct = default)
    {
        var node = _topology.GetNode(heartbeat.NodeId);
        if (node != null)
        {
            var updated = node with
            {
                FreeBytes = heartbeat.FreeBytes,
                HealthScore = heartbeat.HealthScore,
                LastHeartbeat = heartbeat.TimestampUtc
            };

            _topology.AddOrUpdateNode(updated);
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ClusterTopology> GetTopologyAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_topology);
    }

    // ITopologyProvider implementation
    /// <inheritdoc />
    public Task<NodeTopology?> GetNodeTopologyAsync(string nodeId, CancellationToken ct = default)
    {
        return Task.FromResult(_topology.GetNode(nodeId));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NodeTopology>> GetAllNodesAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_topology.GetAllNodes());
    }

    /// <inheritdoc />
    public Task<NodeTopology?> GetSelfTopologyAsync(CancellationToken ct = default)
    {
        var self = _membership.GetSelf();
        return Task.FromResult(_topology.GetNode(self.NodeId));
    }

    private async Task RunHealthCheckLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _healthCheckTimer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await CheckNodeHealthAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckNodeHealthAsync(CancellationToken ct)
    {
        var nodes = _topology.GetAllNodes();
        var staleThreshold = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(_config.HeartbeatTimeoutSeconds);

        foreach (var node in nodes)
        {
            if (node.LastHeartbeat < staleThreshold)
            {
                // Mark node as degraded
                var degraded = node with { HealthScore = Math.Max(0.0, node.HealthScore - 0.2) };
                _topology.AddOrUpdateNode(degraded);

                if (degraded.HealthScore <= 0.0)
                {
                    var message = new PluginMessage
                    {
                        Type = "federation.node.failed",
                        Payload = new Dictionary<string, object>
                        {
                            ["nodeId"] = node.NodeId
                        }
                    };
                    await _messageBus.PublishAsync("federation.node.failed", message, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private void HandleMembershipChanged(ClusterMembershipEvent e)
    {
        // Sync cluster membership changes to topology
        if (e.EventType == ClusterMembershipEventType.NodeDead)
        {
            _topology.RemoveNode(e.Node.NodeId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _healthCheckTimer.Dispose();
    }
}

/// <summary>
/// Configuration options for the FederationOrchestrator.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Federation orchestrator configuration")]
public sealed record FederationOrchestratorConfiguration
{
    /// <summary>
    /// Gets the interval (in seconds) between health check cycles.
    /// </summary>
    /// <remarks>Default: 10 seconds.</remarks>
    public int HealthCheckIntervalSeconds { get; init; } = 10;

    /// <summary>
    /// Gets the timeout (in seconds) after which a node is considered stale and degraded.
    /// </summary>
    /// <remarks>Default: 30 seconds.</remarks>
    public int HeartbeatTimeoutSeconds { get; init; } = 30;
}
