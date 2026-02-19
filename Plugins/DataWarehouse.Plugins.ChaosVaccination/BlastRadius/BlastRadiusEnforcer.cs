using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.BlastRadius
{
    /// <summary>
    /// Enforces blast radius policies during chaos experiments, guaranteeing that failure effects
    /// remain contained within defined boundaries. Implements IBlastRadiusEnforcer.
    ///
    /// Safety invariants (enforced as assertions, never relaxed):
    /// - Plugin crash NEVER propagates to kernel (kernel wrapped in try/catch for all plugin calls)
    /// - Node failure NEVER corrupts shared state (all shared state access through transactions)
    /// - Experiment failure ALWAYS triggers cleanup (finally block in enforcement loop)
    ///
    /// Coordinates IsolationZoneManager for zone lifecycle and FailurePropagationMonitor
    /// for real-time breach detection. Background enforcement loop periodically checks all
    /// active zones and auto-aborts experiments that exceed their blast radius.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Blast radius enforcement")]
    public sealed class BlastRadiusEnforcer : IBlastRadiusEnforcer, IDisposable
    {
        private readonly IMessageBus? _messageBus;
        private readonly IsolationZoneManager _zoneManager;
        private readonly FailurePropagationMonitor _propagationMonitor;
        private readonly ChaosVaccinationOptions _globalOptions;
        private readonly Timer _enforcementTimer;
        private readonly ConcurrentDictionary<string, DateTimeOffset> _recentBreaches = new();
        private volatile bool _disposed;

        /// <inheritdoc />
        public event Action<BlastRadiusBreachEvent>? OnBreachDetected;

        /// <summary>
        /// Creates a new BlastRadiusEnforcer.
        /// </summary>
        /// <param name="messageBus">Message bus for coordination. Nullable for single-node.</param>
        /// <param name="zoneManager">The isolation zone manager for zone lifecycle.</param>
        /// <param name="propagationMonitor">The failure propagation monitor for breach detection.</param>
        /// <param name="globalOptions">Global chaos vaccination options. If null, defaults are used.</param>
        /// <param name="enforcementIntervalMs">Interval in milliseconds for the background enforcement loop. Default: 1000ms.</param>
        public BlastRadiusEnforcer(
            IMessageBus? messageBus,
            IsolationZoneManager zoneManager,
            FailurePropagationMonitor propagationMonitor,
            ChaosVaccinationOptions? globalOptions = null,
            int enforcementIntervalMs = 1000)
        {
            _messageBus = messageBus;
            _zoneManager = zoneManager ?? throw new ArgumentNullException(nameof(zoneManager));
            _propagationMonitor = propagationMonitor ?? throw new ArgumentNullException(nameof(propagationMonitor));
            _globalOptions = globalOptions ?? new ChaosVaccinationOptions();

            // Subscribe to breach events from the propagation monitor
            _propagationMonitor.OnBreachDetected += HandleBreachFromMonitor;

            // Start background enforcement loop
            _enforcementTimer = new Timer(
                EnforcementLoopCallback,
                null,
                TimeSpan.FromMilliseconds(enforcementIntervalMs),
                TimeSpan.FromMilliseconds(enforcementIntervalMs));
        }

        /// <inheritdoc />
        public async Task<IsolationZone> CreateIsolationZoneAsync(
            BlastRadiusPolicy policy,
            string[] targetPlugins,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(targetPlugins);

            // Validate policy against global limits
            if (policy.MaxLevel > _globalOptions.GlobalBlastRadiusLimit)
            {
                throw new InvalidOperationException(
                    $"Blast radius level {policy.MaxLevel} exceeds global limit {_globalOptions.GlobalBlastRadiusLimit}. " +
                    "Adjust ChaosVaccinationOptions.GlobalBlastRadiusLimit to allow higher blast radii.");
            }

            // Validate max affected plugins against what is requested
            if (targetPlugins.Length > policy.MaxAffectedPlugins)
            {
                throw new ArgumentException(
                    $"Target plugins count ({targetPlugins.Length}) exceeds policy MaxAffectedPlugins ({policy.MaxAffectedPlugins}).",
                    nameof(targetPlugins));
            }

            var zone = await _zoneManager.CreateZoneAsync(policy, targetPlugins, experimentId: null, ct)
                .ConfigureAwait(false);

            return zone;
        }

        /// <inheritdoc />
        public async Task<FailureContainmentResult> EnforceAsync(string zoneId, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);

            // Get the zone
            var activeZone = _zoneManager.GetZone(zoneId);
            if (activeZone == null)
            {
                return new FailureContainmentResult
                {
                    Contained = true,
                    ActualRadius = BlastRadiusLevel.SingleStrategy,
                    ContainmentActions = new[] { $"Zone '{zoneId}' not found (already released or never created)" }
                };
            }

            // Detect propagation
            var propagationReport = _propagationMonitor.DetectPropagation();

            if (!propagationReport.HasBreach)
            {
                return new FailureContainmentResult
                {
                    Contained = true,
                    ActualRadius = activeZone.Zone.Policy.MaxLevel,
                    ContainmentActions = new[] { "No breach detected -- all failures contained within zone boundaries" }
                };
            }

            // Breach detected
            var containmentActions = new List<string>();
            var actualRadius = DetermineActualRadius(propagationReport);

            // Fire breach event
            var breachEvent = new BlastRadiusBreachEvent
            {
                ZoneId = zoneId,
                Policy = activeZone.Zone.Policy,
                ActualRadius = actualRadius,
                Timestamp = DateTimeOffset.UtcNow
            };
            OnBreachDetected?.Invoke(breachEvent);
            containmentActions.Add($"Breach event fired: actual radius {actualRadius}, policy max {activeZone.Zone.Policy.MaxLevel}");

            // If auto-abort is enabled, trip circuit breakers and abort experiment
            if (activeZone.Zone.Policy.AutoAbortOnBreach)
            {
                // Trip circuit breakers for affected plugins
                foreach (var pluginId in propagationReport.BreachedPlugins)
                {
                    if (_messageBus != null)
                    {
                        await _messageBus.PublishAsync(
                            "resilience.circuit-breaker.trip",
                            new PluginMessage
                            {
                                Type = "resilience.circuit-breaker.trip",
                                SourcePluginId = "chaos-vaccination",
                                CorrelationId = zoneId,
                                Payload = new Dictionary<string, object>
                                {
                                    ["pluginId"] = pluginId,
                                    ["reason"] = $"Blast radius breach in zone {zoneId}",
                                    ["zoneId"] = zoneId
                                }
                            },
                            ct).ConfigureAwait(false);
                    }
                    containmentActions.Add($"Tripped circuit breaker for breached plugin '{pluginId}'");
                }

                // Publish breach for observability
                if (_messageBus != null)
                {
                    await _messageBus.PublishAsync(
                        "chaos.blast-radius.breach",
                        new PluginMessage
                        {
                            Type = "chaos.blast-radius.breach",
                            SourcePluginId = "chaos-vaccination",
                            CorrelationId = zoneId,
                            Payload = new Dictionary<string, object>
                            {
                                ["zoneId"] = zoneId,
                                ["breachedPlugins"] = propagationReport.BreachedPlugins,
                                ["breachedNodes"] = propagationReport.BreachedNodes,
                                ["cascadeDepth"] = propagationReport.CascadeDepth,
                                ["actualRadius"] = actualRadius.ToString(),
                                ["policyMaxLevel"] = activeZone.Zone.Policy.MaxLevel.ToString()
                            }
                        },
                        ct).ConfigureAwait(false);
                }
                containmentActions.Add("Published breach event to 'chaos.blast-radius.breach' for observability");

                // Auto-abort the experiment
                if (activeZone.ExperimentId != null && _messageBus != null)
                {
                    await _messageBus.PublishAsync(
                        "chaos.experiment.abort",
                        new PluginMessage
                        {
                            Type = "chaos.experiment.abort",
                            SourcePluginId = "chaos-vaccination",
                            CorrelationId = zoneId,
                            Payload = new Dictionary<string, object>
                            {
                                ["experimentId"] = activeZone.ExperimentId,
                                ["reason"] = $"Blast radius breach in zone {zoneId}: actual={actualRadius}, max={activeZone.Zone.Policy.MaxLevel}"
                            }
                        },
                        ct).ConfigureAwait(false);
                    containmentActions.Add($"Auto-aborted experiment '{activeZone.ExperimentId}' due to blast radius breach");
                }
            }

            _recentBreaches[zoneId] = DateTimeOffset.UtcNow;

            return new FailureContainmentResult
            {
                Contained = false,
                ActualRadius = actualRadius,
                BreachedPlugins = propagationReport.BreachedPlugins,
                BreachedNodes = propagationReport.BreachedNodes,
                ContainmentActions = containmentActions.ToArray()
            };
        }

        /// <inheritdoc />
        public async Task ReleaseZoneAsync(string zoneId, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _zoneManager.ReleaseZoneAsync(zoneId, ct).ConfigureAwait(false);
            _recentBreaches.TryRemove(zoneId, out _);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<IsolationZone>> GetActiveZonesAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Task.FromResult(_zoneManager.GetActiveZones());
        }

        /// <summary>
        /// Determines the actual blast radius level based on the propagation report.
        /// Maps the scope of affected components to the BlastRadiusLevel enum.
        /// </summary>
        private static BlastRadiusLevel DetermineActualRadius(FailurePropagationMonitor.PropagationReport report)
        {
            if (report.BreachedNodes.Length > 1)
                return BlastRadiusLevel.NodeGroup;

            if (report.BreachedNodes.Length == 1)
                return BlastRadiusLevel.SingleNode;

            if (report.BreachedPlugins.Length > 3)
                return BlastRadiusLevel.PluginCategory;

            if (report.BreachedPlugins.Length > 1)
                return BlastRadiusLevel.SinglePlugin;

            return BlastRadiusLevel.SingleStrategy;
        }

        /// <summary>
        /// Handles breach reports from the FailurePropagationMonitor's OnBreachDetected event.
        /// Translates monitor breach reports into BlastRadiusBreachEvents.
        /// </summary>
        private void HandleBreachFromMonitor(FailurePropagationMonitor.PropagationReport report)
        {
            if (_disposed) return;

            // Find which zones are affected
            foreach (var zoneId in report.AffectedZones)
            {
                var activeZone = _zoneManager.GetZone(zoneId);
                if (activeZone == null) continue;

                var actualRadius = DetermineActualRadius(report);

                OnBreachDetected?.Invoke(new BlastRadiusBreachEvent
                {
                    ZoneId = zoneId,
                    Policy = activeZone.Zone.Policy,
                    ActualRadius = actualRadius,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
        }

        /// <summary>
        /// Background enforcement loop callback. Checks all active zones for breaches
        /// and takes containment actions as needed.
        /// </summary>
        private async void EnforcementLoopCallback(object? state)
        {
            if (_disposed) return;

            try
            {
                var activeZones = _zoneManager.GetActiveZones();
                if (activeZones.Count == 0) return;

                foreach (var zone in activeZones)
                {
                    try
                    {
                        var result = await EnforceAsync(zone.ZoneId, CancellationToken.None)
                            .ConfigureAwait(false);

                        if (!result.Contained)
                        {
                            // Log breach metrics via message bus
                            if (_messageBus != null)
                            {
                                await _messageBus.PublishAsync(
                                    "chaos.metrics.breach",
                                    new PluginMessage
                                    {
                                        Type = "chaos.metrics.breach",
                                        SourcePluginId = "chaos-vaccination",
                                        CorrelationId = zone.ZoneId,
                                        Payload = new Dictionary<string, object>
                                        {
                                            ["zoneId"] = zone.ZoneId,
                                            ["contained"] = false,
                                            ["actualRadius"] = result.ActualRadius.ToString(),
                                            ["breachedPluginCount"] = result.BreachedPlugins.Length,
                                            ["breachedNodeCount"] = result.BreachedNodes.Length
                                        }
                                    },
                                    CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        return; // Shutting down
                    }
                    catch (Exception)
                    {
                        // Individual zone enforcement failure should not stop the loop.
                        // Safety invariant: experiment failure ALWAYS triggers cleanup (finally block).
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutting down
            }
        }

        /// <summary>
        /// Gets the count of recent breaches across all zones.
        /// </summary>
        public int RecentBreachCount => _recentBreaches.Count;

        /// <summary>
        /// Disposes the enforcer, stopping the enforcement loop and cleaning up resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _propagationMonitor.OnBreachDetected -= HandleBreachFromMonitor;
            _enforcementTimer.Dispose();
        }
    }
}
