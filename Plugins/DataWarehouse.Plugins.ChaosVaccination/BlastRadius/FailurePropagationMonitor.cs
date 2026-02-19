using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.BlastRadius
{
    /// <summary>
    /// Monitors failure propagation in real-time by subscribing to message bus topics for plugin errors,
    /// node health changes, cluster membership, and circuit breaker state transitions.
    /// Detects when failures escape their intended blast radius (breach detection).
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Blast radius enforcement")]
    public sealed class FailurePropagationMonitor : IDisposable
    {
        private readonly ConcurrentDictionary<string, FailureEvent> _recentFailures = new();
        private readonly IsolationZoneManager _zoneManager;
        private readonly IMessageBus? _messageBus;
        private readonly TimeSpan _slidingWindow;
        private readonly Timer _windowCleanupTimer;
        private readonly List<IDisposable> _subscriptions = new();
        private volatile bool _disposed;

        /// <summary>
        /// Represents a failure event tracked by the monitor.
        /// </summary>
        public sealed record FailureEvent
        {
            /// <summary>Unique identifier for this failure event.</summary>
            public required string Id { get; init; }

            /// <summary>The plugin that experienced the failure.</summary>
            public required string PluginId { get; init; }

            /// <summary>The node where the failure occurred.</summary>
            public required string NodeId { get; init; }

            /// <summary>The type of fault observed.</summary>
            public required FaultType FaultType { get; init; }

            /// <summary>When the failure was observed.</summary>
            public required DateTimeOffset Timestamp { get; init; }

            /// <summary>The experiment that caused this failure, if known.</summary>
            public string? OriginExperimentId { get; init; }
        }

        /// <summary>
        /// Report of failure propagation analysis.
        /// </summary>
        public sealed record PropagationReport
        {
            /// <summary>Whether failures have escaped any active isolation zone.</summary>
            public required bool HasBreach { get; init; }

            /// <summary>Plugin IDs affected outside their intended zone.</summary>
            public string[] BreachedPlugins { get; init; } = Array.Empty<string>();

            /// <summary>Node IDs affected outside their intended zone.</summary>
            public string[] BreachedNodes { get; init; } = Array.Empty<string>();

            /// <summary>How many levels deep the failure cascade has propagated.</summary>
            public required int CascadeDepth { get; init; }

            /// <summary>The identified root cause plugin, if determinable.</summary>
            public string? RootCause { get; init; }

            /// <summary>Zone IDs affected by the propagation.</summary>
            public string[] AffectedZones { get; init; } = Array.Empty<string>();

            /// <summary>Total number of failure events in the current sliding window.</summary>
            public required int TotalFailuresInWindow { get; init; }
        }

        /// <summary>
        /// Raised when a blast radius breach is detected (failures escaping their zone).
        /// </summary>
        public event Action<PropagationReport>? OnBreachDetected;

        /// <summary>
        /// Creates a new FailurePropagationMonitor.
        /// </summary>
        /// <param name="zoneManager">The isolation zone manager to check containment against.</param>
        /// <param name="messageBus">Message bus for subscribing to failure events. Nullable for single-node.</param>
        /// <param name="slidingWindowSeconds">Sliding window in seconds for failure correlation. Default: 30.</param>
        public FailurePropagationMonitor(
            IsolationZoneManager zoneManager,
            IMessageBus? messageBus = null,
            int slidingWindowSeconds = 30)
        {
            _zoneManager = zoneManager ?? throw new ArgumentNullException(nameof(zoneManager));
            _messageBus = messageBus;
            _slidingWindow = TimeSpan.FromSeconds(slidingWindowSeconds);

            // Cleanup expired events every 5 seconds
            _windowCleanupTimer = new Timer(
                CleanupExpiredEvents,
                null,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(5));

            SubscribeToFailureEvents();
        }

        /// <summary>
        /// Subscribes to relevant message bus topics for failure detection.
        /// </summary>
        private void SubscribeToFailureEvents()
        {
            if (_messageBus == null) return;

            try
            {
                // Subscribe to plugin error events
                _subscriptions.Add(_messageBus.SubscribePattern("plugin.error.*", OnPluginError));

                // Subscribe to node health events
                _subscriptions.Add(_messageBus.SubscribePattern("node.health.*", OnNodeHealthChange));

                // Subscribe to cluster membership events
                _subscriptions.Add(_messageBus.SubscribePattern("cluster.membership.*", OnClusterMembershipChange));

                // Subscribe to circuit breaker state changes
                _subscriptions.Add(_messageBus.Subscribe(
                    "resilience.circuit-breaker.state-changed",
                    OnCircuitBreakerStateChanged));
            }
            catch (NotSupportedException)
            {
                // Pattern subscriptions not supported by this bus implementation;
                // fall back to specific topic subscriptions
                _subscriptions.Add(_messageBus.Subscribe("plugin.error", OnPluginError));
                _subscriptions.Add(_messageBus.Subscribe("node.health", OnNodeHealthChange));
                _subscriptions.Add(_messageBus.Subscribe("cluster.membership", OnClusterMembershipChange));
                _subscriptions.Add(_messageBus.Subscribe(
                    "resilience.circuit-breaker.state-changed",
                    OnCircuitBreakerStateChanged));
            }
        }

        /// <summary>
        /// Records a failure event from a plugin error message.
        /// </summary>
        private System.Threading.Tasks.Task OnPluginError(PluginMessage msg)
        {
            var pluginId = msg.Payload.TryGetValue("pluginId", out var pid) ? pid?.ToString() ?? msg.SourcePluginId : msg.SourcePluginId;
            var nodeId = msg.Payload.TryGetValue("nodeId", out var nid) ? nid?.ToString() ?? "local" : "local";
            var experimentId = msg.Payload.TryGetValue("experimentId", out var eid) ? eid?.ToString() : null;

            RecordFailure(pluginId, nodeId, FaultType.PluginCrash, experimentId);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// Records a failure event from a node health change message.
        /// </summary>
        private System.Threading.Tasks.Task OnNodeHealthChange(PluginMessage msg)
        {
            var nodeId = msg.Payload.TryGetValue("nodeId", out var nid) ? nid?.ToString() ?? "unknown" : "unknown";
            var status = msg.Payload.TryGetValue("status", out var s) ? s?.ToString() : null;

            if (string.Equals(status, "unhealthy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "down", StringComparison.OrdinalIgnoreCase))
            {
                RecordFailure("node-health", nodeId, FaultType.NodeCrash, null);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// Records a failure event from a cluster membership change message.
        /// </summary>
        private System.Threading.Tasks.Task OnClusterMembershipChange(PluginMessage msg)
        {
            var nodeId = msg.Payload.TryGetValue("nodeId", out var nid) ? nid?.ToString() ?? "unknown" : "unknown";
            var eventType = msg.Payload.TryGetValue("event", out var e) ? e?.ToString() : null;

            if (string.Equals(eventType, "left", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(eventType, "failed", StringComparison.OrdinalIgnoreCase))
            {
                RecordFailure("cluster-membership", nodeId, FaultType.NodeCrash, null);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// Records a failure event from a circuit breaker state change to Open.
        /// </summary>
        private System.Threading.Tasks.Task OnCircuitBreakerStateChanged(PluginMessage msg)
        {
            var newState = msg.Payload.TryGetValue("newState", out var ns) ? ns?.ToString() : null;

            if (string.Equals(newState, "Open", StringComparison.OrdinalIgnoreCase))
            {
                var pluginId = msg.Payload.TryGetValue("pluginId", out var pid) ? pid?.ToString() ?? "unknown" : "unknown";
                var nodeId = msg.Payload.TryGetValue("nodeId", out var nid) ? nid?.ToString() ?? "local" : "local";

                RecordFailure(pluginId, nodeId, FaultType.PluginCrash, null);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// Records a failure event into the sliding window tracker.
        /// </summary>
        /// <param name="pluginId">The affected plugin.</param>
        /// <param name="nodeId">The affected node.</param>
        /// <param name="faultType">The type of fault observed.</param>
        /// <param name="experimentId">The originating experiment, if known.</param>
        public void RecordFailure(string pluginId, string nodeId, FaultType faultType, string? experimentId)
        {
            if (_disposed) return;

            var failureEvent = new FailureEvent
            {
                Id = $"fail-{Guid.NewGuid():N}",
                PluginId = pluginId,
                NodeId = nodeId,
                FaultType = faultType,
                Timestamp = DateTimeOffset.UtcNow,
                OriginExperimentId = experimentId
            };

            _recentFailures.TryAdd(failureEvent.Id, failureEvent);

            // Check for breach after recording
            var report = DetectPropagation();
            if (report.HasBreach)
            {
                OnBreachDetected?.Invoke(report);
            }
        }

        /// <summary>
        /// Analyzes failure events within the sliding window to detect propagation beyond zone boundaries.
        /// </summary>
        /// <returns>A propagation report indicating whether breaches have occurred.</returns>
        public PropagationReport DetectPropagation()
        {
            var cutoff = DateTimeOffset.UtcNow - _slidingWindow;
            var windowFailures = _recentFailures.Values
                .Where(f => f.Timestamp >= cutoff)
                .ToList();

            if (windowFailures.Count == 0)
            {
                return new PropagationReport
                {
                    HasBreach = false,
                    CascadeDepth = 0,
                    TotalFailuresInWindow = 0
                };
            }

            var containedPluginIds = _zoneManager.GetAllContainedPluginIds();
            var breachedPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var breachedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var affectedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Group failures by plugin to detect cross-zone propagation
            var failuresByPlugin = windowFailures
                .GroupBy(f => f.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in failuresByPlugin)
            {
                var pluginId = kvp.Key;
                var pluginFailures = kvp.Value;

                // Skip internal tracking identifiers
                if (string.Equals(pluginId, "node-health", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pluginId, "cluster-membership", StringComparison.OrdinalIgnoreCase))
                {
                    // Check node-level breaches
                    foreach (var failure in pluginFailures)
                    {
                        // Node failures outside any zone are breaches
                        var anyZoneContainsNode = false;
                        foreach (var zone in _zoneManager.GetActiveZones())
                        {
                            if (zone.ContainedNodes.Contains(failure.NodeId, StringComparer.OrdinalIgnoreCase))
                            {
                                anyZoneContainsNode = true;
                                break;
                            }
                        }

                        if (!anyZoneContainsNode && _zoneManager.GetActiveZones().Count > 0)
                        {
                            breachedNodes.Add(failure.NodeId);
                        }
                    }
                    continue;
                }

                var pluginZone = _zoneManager.FindZoneForPlugin(pluginId);

                if (pluginZone != null)
                {
                    // Plugin is in a zone -- failures within the zone are OK
                    affectedZones.Add(pluginZone);
                }
                else if (containedPluginIds.Count > 0)
                {
                    // Plugin is NOT in any zone, but zones exist -- this is a BREACH
                    // Only count as breach if there are active experiments (zones exist)
                    breachedPlugins.Add(pluginId);
                }
            }

            // Calculate cascade depth: number of distinct plugins affected in the window
            var cascadeDepth = failuresByPlugin.Count;

            // Determine root cause: the earliest failure in the window
            var rootCauseEvent = windowFailures.OrderBy(f => f.Timestamp).FirstOrDefault();

            return new PropagationReport
            {
                HasBreach = breachedPlugins.Count > 0 || breachedNodes.Count > 0,
                BreachedPlugins = breachedPlugins.ToArray(),
                BreachedNodes = breachedNodes.ToArray(),
                CascadeDepth = cascadeDepth,
                RootCause = rootCauseEvent?.PluginId,
                AffectedZones = affectedZones.ToArray(),
                TotalFailuresInWindow = windowFailures.Count
            };
        }

        /// <summary>
        /// Gets the count of failure events currently in the sliding window.
        /// </summary>
        public int ActiveFailureCount
        {
            get
            {
                var cutoff = DateTimeOffset.UtcNow - _slidingWindow;
                return _recentFailures.Values.Count(f => f.Timestamp >= cutoff);
            }
        }

        /// <summary>
        /// Clears all tracked failure events.
        /// </summary>
        public void ClearFailures()
        {
            _recentFailures.Clear();
        }

        /// <summary>
        /// Timer callback to remove expired failure events from the sliding window.
        /// </summary>
        private void CleanupExpiredEvents(object? state)
        {
            if (_disposed) return;

            try
            {
                var cutoff = DateTimeOffset.UtcNow - _slidingWindow;
                var expiredIds = _recentFailures
                    .Where(kvp => kvp.Value.Timestamp < cutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var id in expiredIds)
                {
                    _recentFailures.TryRemove(id, out _);
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutting down
            }
        }

        /// <summary>
        /// Disposes the monitor, stopping the cleanup timer and removing subscriptions.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _windowCleanupTimer.Dispose();

            foreach (var subscription in _subscriptions)
            {
                try { subscription.Dispose(); }
                catch { /* Best effort cleanup */ }
            }
            _subscriptions.Clear();
        }
    }
}
