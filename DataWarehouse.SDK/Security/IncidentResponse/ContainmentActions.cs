using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Security.IncidentResponse
{
    /// <summary>
    /// Interface for containment actions that can be executed during incident response.
    /// Each action communicates via the message bus for actual enforcement.
    /// </summary>
    public interface IContainmentAction
    {
        /// <summary>Unique identifier for this action type.</summary>
        string ActionId { get; }

        /// <summary>Human-readable description of what this action does.</summary>
        string Description { get; }

        /// <summary>
        /// Executes the containment action.
        /// Publishes enforcement commands to the message bus and records audit entries.
        /// </summary>
        Task<ContainmentResult> ExecuteAsync(ContainmentContext ctx, CancellationToken ct = default);

        /// <summary>
        /// Rolls back the containment action if possible.
        /// Not all actions support rollback (e.g., credential revocation may not be reversible).
        /// </summary>
        Task<ContainmentResult> RollbackAsync(ContainmentContext ctx, CancellationToken ct = default);
    }

    /// <summary>
    /// Context provided to containment actions during execution.
    /// Contains the incident details and targeting information.
    /// </summary>
    public sealed record ContainmentContext
    {
        /// <summary>The incident that triggered this containment action.</summary>
        public required string IncidentId { get; init; }

        /// <summary>Severity of the incident.</summary>
        public required IncidentSeverity Severity { get; init; }

        /// <summary>Target cluster node to act upon (for node isolation).</summary>
        public string? TargetNodeId { get; init; }

        /// <summary>Target user to act upon (for credential revocation).</summary>
        public string? TargetUserId { get; init; }

        /// <summary>Target IP address to act upon (for IP blocking).</summary>
        public string? TargetIpAddress { get; init; }

        /// <summary>Target data key to act upon (for data quarantine).</summary>
        public string? TargetDataKey { get; init; }

        /// <summary>Additional context metadata.</summary>
        public Dictionary<string, string> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Result of a containment action execution or rollback.
    /// </summary>
    public sealed record ContainmentResult
    {
        /// <summary>Whether the action completed successfully.</summary>
        public required bool Success { get; init; }

        /// <summary>The action that was executed.</summary>
        public required string ActionId { get; init; }

        /// <summary>When the action was executed.</summary>
        public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Human-readable details of what happened.</summary>
        public required string Details { get; init; }

        /// <summary>Whether this action can be rolled back.</summary>
        public bool RollbackAvailable { get; init; }

        /// <summary>Error message if the action failed.</summary>
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Severity levels for security incidents.
    /// </summary>
    public enum IncidentSeverity
    {
        /// <summary>Low severity: minor policy violations, informational alerts.</summary>
        Low = 1,

        /// <summary>Medium severity: suspicious activity requiring investigation.</summary>
        Medium = 2,

        /// <summary>High severity: confirmed security event requiring containment.</summary>
        High = 3,

        /// <summary>Critical severity: active breach or immediate threat.</summary>
        Critical = 4
    }

    /// <summary>
    /// Isolates a cluster node by removing it from SWIM membership and blocking connections.
    /// Publishes "cluster.node.isolate" to the message bus for enforcement.
    /// </summary>
    public sealed class IsolateNodeAction : IContainmentAction
    {
        private readonly IMessageBus _messageBus;

        public IsolateNodeAction(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public string ActionId => "isolate-node";
        public string Description => "Isolate cluster node: remove from SWIM membership, block incoming connections";

        public async Task<ContainmentResult> ExecuteAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(ctx.TargetNodeId))
            {
                return new ContainmentResult
                {
                    Success = false,
                    ActionId = ActionId,
                    Details = "No target node specified",
                    ErrorMessage = "TargetNodeId is required for node isolation"
                };
            }

            await _messageBus.PublishAsync("cluster.node.isolate", new PluginMessage
            {
                Type = "cluster.node.isolate",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["nodeId"] = ctx.TargetNodeId,
                    ["severity"] = ctx.Severity.ToString(),
                    ["action"] = "isolate"
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"Node '{ctx.TargetNodeId}' isolation command published for incident {ctx.IncidentId}",
                RollbackAvailable = true
            };
        }

        public async Task<ContainmentResult> RollbackAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            await _messageBus.PublishAsync("cluster.node.rejoin", new PluginMessage
            {
                Type = "cluster.node.rejoin",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["nodeId"] = ctx.TargetNodeId!,
                    ["action"] = "rejoin"
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"Node '{ctx.TargetNodeId}' rejoin command published",
                RollbackAvailable = false
            };
        }
    }

    /// <summary>
    /// Revokes credentials for a target user: invalidates sessions, rotates API keys.
    /// Publishes "auth.credentials.revoke" to the message bus.
    /// </summary>
    public sealed class RevokeCredentialsAction : IContainmentAction
    {
        private readonly IMessageBus _messageBus;

        public RevokeCredentialsAction(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public string ActionId => "revoke-credentials";
        public string Description => "Revoke credentials: invalidate all sessions, rotate affected API keys";

        public async Task<ContainmentResult> ExecuteAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(ctx.TargetUserId))
            {
                return new ContainmentResult
                {
                    Success = false,
                    ActionId = ActionId,
                    Details = "No target user specified",
                    ErrorMessage = "TargetUserId is required for credential revocation"
                };
            }

            await _messageBus.PublishAsync("auth.credentials.revoke", new PluginMessage
            {
                Type = "auth.credentials.revoke",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["userId"] = ctx.TargetUserId,
                    ["severity"] = ctx.Severity.ToString(),
                    ["invalidateSessions"] = true,
                    ["rotateApiKeys"] = true
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"Credential revocation published for user '{ctx.TargetUserId}' in incident {ctx.IncidentId}",
                RollbackAvailable = false // Credential revocation is not reversible
            };
        }

        public Task<ContainmentResult> RollbackAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            return Task.FromResult(new ContainmentResult
            {
                Success = false,
                ActionId = ActionId,
                Details = "Credential revocation cannot be rolled back; new credentials must be issued manually",
                RollbackAvailable = false
            });
        }
    }

    /// <summary>
    /// Blocks an IP address: adds to deny list, terminates active connections.
    /// Publishes "network.ip.block" to the message bus.
    /// </summary>
    public sealed class BlockIpAction : IContainmentAction
    {
        private readonly IMessageBus _messageBus;

        public BlockIpAction(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public string ActionId => "block-ip";
        public string Description => "Block IP address: add to deny list, terminate active connections from that IP";

        public async Task<ContainmentResult> ExecuteAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(ctx.TargetIpAddress))
            {
                return new ContainmentResult
                {
                    Success = false,
                    ActionId = ActionId,
                    Details = "No target IP address specified",
                    ErrorMessage = "TargetIpAddress is required for IP blocking"
                };
            }

            await _messageBus.PublishAsync("network.ip.block", new PluginMessage
            {
                Type = "network.ip.block",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["ipAddress"] = ctx.TargetIpAddress,
                    ["severity"] = ctx.Severity.ToString(),
                    ["terminateConnections"] = true
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"IP block command published for '{ctx.TargetIpAddress}' in incident {ctx.IncidentId}",
                RollbackAvailable = true
            };
        }

        public async Task<ContainmentResult> RollbackAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            await _messageBus.PublishAsync("network.ip.unblock", new PluginMessage
            {
                Type = "network.ip.unblock",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["ipAddress"] = ctx.TargetIpAddress!,
                    ["action"] = "unblock"
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"IP unblock command published for '{ctx.TargetIpAddress}'",
                RollbackAvailable = false
            };
        }
    }

    /// <summary>
    /// Quarantines data: moves to quarantine storage, removes from active indexes.
    /// Publishes "storage.data.quarantine" to the message bus.
    /// </summary>
    public sealed class QuarantineDataAction : IContainmentAction
    {
        private readonly IMessageBus _messageBus;

        public QuarantineDataAction(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public string ActionId => "quarantine-data";
        public string Description => "Quarantine data: move to quarantine storage, remove from active indexes";

        public async Task<ContainmentResult> ExecuteAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(ctx.TargetDataKey))
            {
                return new ContainmentResult
                {
                    Success = false,
                    ActionId = ActionId,
                    Details = "No target data key specified",
                    ErrorMessage = "TargetDataKey is required for data quarantine"
                };
            }

            await _messageBus.PublishAsync("storage.data.quarantine", new PluginMessage
            {
                Type = "storage.data.quarantine",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["dataKey"] = ctx.TargetDataKey,
                    ["severity"] = ctx.Severity.ToString(),
                    ["removeFromIndex"] = true
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"Data quarantine command published for key '{ctx.TargetDataKey}' in incident {ctx.IncidentId}",
                RollbackAvailable = true
            };
        }

        public async Task<ContainmentResult> RollbackAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            await _messageBus.PublishAsync("storage.data.restore", new PluginMessage
            {
                Type = "storage.data.restore",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["dataKey"] = ctx.TargetDataKey!,
                    ["action"] = "restore"
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"Data restore command published for key '{ctx.TargetDataKey}'",
                RollbackAvailable = false
            };
        }
    }

    /// <summary>
    /// Puts the system in read-only mode: blocks all writes while allowing reads.
    /// Publishes "system.mode.readonly" to the message bus.
    /// </summary>
    public sealed class ReadOnlyModeAction : IContainmentAction
    {
        private readonly IMessageBus _messageBus;

        public ReadOnlyModeAction(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public string ActionId => "readonly-mode";
        public string Description => "Read-only mode: block all write operations, allow reads only";

        public async Task<ContainmentResult> ExecuteAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            await _messageBus.PublishAsync("system.mode.readonly", new PluginMessage
            {
                Type = "system.mode.readonly",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["severity"] = ctx.Severity.ToString(),
                    ["enabled"] = true
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"Read-only mode enabled for incident {ctx.IncidentId}",
                RollbackAvailable = true
            };
        }

        public async Task<ContainmentResult> RollbackAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            await _messageBus.PublishAsync("system.mode.readwrite", new PluginMessage
            {
                Type = "system.mode.readwrite",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["enabled"] = false,
                    ["action"] = "restore-readwrite"
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = "Read-write mode restored",
                RollbackAvailable = false
            };
        }
    }

    /// <summary>
    /// Captures an audit snapshot of current system state for forensic analysis.
    /// Publishes "audit.snapshot.create" to the message bus.
    /// </summary>
    public sealed class AuditSnapshotAction : IContainmentAction
    {
        private readonly IMessageBus _messageBus;

        public AuditSnapshotAction(IMessageBus messageBus)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        }

        public string ActionId => "audit-snapshot";
        public string Description => "Audit snapshot: capture current system state for forensic analysis";

        public async Task<ContainmentResult> ExecuteAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            await _messageBus.PublishAsync("audit.snapshot.create", new PluginMessage
            {
                Type = "audit.snapshot.create",
                Source = "IncidentResponse",
                Payload = new Dictionary<string, object>
                {
                    ["incidentId"] = ctx.IncidentId,
                    ["severity"] = ctx.Severity.ToString(),
                    ["snapshotId"] = Guid.NewGuid().ToString("N"),
                    ["captureTimestamp"] = DateTimeOffset.UtcNow.ToString("o")
                }
            }, ct);

            return new ContainmentResult
            {
                Success = true,
                ActionId = ActionId,
                Details = $"Audit snapshot capture initiated for incident {ctx.IncidentId}",
                RollbackAvailable = false // Snapshots cannot be un-taken
            };
        }

        public Task<ContainmentResult> RollbackAsync(ContainmentContext ctx, CancellationToken ct = default)
        {
            return Task.FromResult(new ContainmentResult
            {
                Success = false,
                ActionId = ActionId,
                Details = "Audit snapshots are immutable and cannot be rolled back",
                RollbackAvailable = false
            });
        }
    }
}
