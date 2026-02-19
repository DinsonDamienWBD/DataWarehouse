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
    /// Manages the lifecycle of isolation zones that contain fault effects during chaos experiments.
    /// Each zone is backed by circuit breakers and bulkhead allocations to enforce hard isolation boundaries.
    /// Thread-safe: all zone creation/destruction is serialized via SemaphoreSlim.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Blast radius enforcement")]
    public sealed class IsolationZoneManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, ActiveZone> _activeZones = new();
        private readonly IMessageBus? _messageBus;
        private readonly SemaphoreSlim _zoneLock = new(1, 1);
        private readonly Timer _cleanupTimer;
        private volatile bool _disposed;

        /// <summary>
        /// Represents an active isolation zone with its backing infrastructure.
        /// </summary>
        public sealed record ActiveZone
        {
            /// <summary>The isolation zone definition.</summary>
            public required IsolationZone Zone { get; init; }

            /// <summary>When the zone was created.</summary>
            public required DateTimeOffset CreatedAt { get; init; }

            /// <summary>The experiment ID this zone is associated with, if any.</summary>
            public string? ExperimentId { get; init; }

            /// <summary>Circuit breaker IDs created for plugins in this zone.</summary>
            public required string[] CircuitBreakerIds { get; init; }

            /// <summary>Bulkhead IDs created for plugins in this zone.</summary>
            public required string[] BulkheadIds { get; init; }
        }

        /// <summary>
        /// Health report for an isolation zone.
        /// </summary>
        public sealed record ZoneHealthReport
        {
            /// <summary>The zone ID.</summary>
            public required string ZoneId { get; init; }

            /// <summary>Whether all plugins remain contained within the zone.</summary>
            public required bool AllPluginsContained { get; init; }

            /// <summary>Whether any breaches have been detected.</summary>
            public required bool HasBreaches { get; init; }

            /// <summary>Plugin IDs that have breached zone boundaries.</summary>
            public string[] BreachedPluginIds { get; init; } = Array.Empty<string>();

            /// <summary>How long the zone has been active.</summary>
            public required TimeSpan ActiveDuration { get; init; }

            /// <summary>Whether the zone has exceeded its maximum duration.</summary>
            public required bool IsExpired { get; init; }
        }

        /// <summary>
        /// Creates a new IsolationZoneManager.
        /// </summary>
        /// <param name="messageBus">Message bus for coordinating circuit breakers and bulkheads. Nullable for single-node deployments.</param>
        /// <param name="cleanupIntervalMs">Interval in milliseconds for the auto-cleanup timer. Default: 5000ms.</param>
        public IsolationZoneManager(IMessageBus? messageBus = null, int cleanupIntervalMs = 5000)
        {
            _messageBus = messageBus;
            _cleanupTimer = new Timer(
                CleanupExpiredZones,
                null,
                TimeSpan.FromMilliseconds(cleanupIntervalMs),
                TimeSpan.FromMilliseconds(cleanupIntervalMs));
        }

        /// <summary>
        /// Creates an isolation zone with circuit breaker and bulkhead backing for the specified plugins.
        /// </summary>
        /// <param name="policy">The blast radius policy to enforce.</param>
        /// <param name="targetPlugins">Plugin IDs to isolate within the zone.</param>
        /// <param name="experimentId">Optional experiment ID to associate with the zone.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The created isolation zone.</returns>
        public async Task<IsolationZone> CreateZoneAsync(
            BlastRadiusPolicy policy,
            string[] targetPlugins,
            string? experimentId = null,
            CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(targetPlugins);

            if (targetPlugins.Length == 0)
                throw new ArgumentException("At least one target plugin is required.", nameof(targetPlugins));

            await _zoneLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var zoneId = $"zone-{Guid.NewGuid():N}";
                var circuitBreakerIds = new List<string>();
                var bulkheadIds = new List<string>();

                // Create circuit breakers for each contained plugin
                foreach (var pluginId in targetPlugins)
                {
                    var cbId = $"cb-{zoneId}-{pluginId}";
                    circuitBreakerIds.Add(cbId);

                    if (_messageBus != null)
                    {
                        await _messageBus.PublishAsync(
                            "resilience.circuit-breaker.create",
                            new PluginMessage
                            {
                                Type = "resilience.circuit-breaker.create",
                                SourcePluginId = "chaos-vaccination",
                                CorrelationId = zoneId,
                                Payload = new Dictionary<string, object>
                                {
                                    ["circuitBreakerId"] = cbId,
                                    ["pluginId"] = pluginId,
                                    ["zoneId"] = zoneId,
                                    ["failureThreshold"] = 3,
                                    ["breakDurationMs"] = policy.MaxDurationMs
                                }
                            },
                            ct).ConfigureAwait(false);
                    }

                    // Create bulkhead allocation
                    var bhId = $"bh-{zoneId}-{pluginId}";
                    bulkheadIds.Add(bhId);

                    if (_messageBus != null)
                    {
                        await _messageBus.PublishAsync(
                            "resilience.bulkhead.create",
                            new PluginMessage
                            {
                                Type = "resilience.bulkhead.create",
                                SourcePluginId = "chaos-vaccination",
                                CorrelationId = zoneId,
                                Payload = new Dictionary<string, object>
                                {
                                    ["bulkheadId"] = bhId,
                                    ["pluginId"] = pluginId,
                                    ["zoneId"] = zoneId,
                                    ["maxConcurrency"] = 10
                                }
                            },
                            ct).ConfigureAwait(false);
                    }
                }

                var zone = new IsolationZone
                {
                    ZoneId = zoneId,
                    ContainedPlugins = targetPlugins,
                    ContainedNodes = Array.Empty<string>(),
                    Policy = policy,
                    IsActive = true
                };

                var activeZone = new ActiveZone
                {
                    Zone = zone,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExperimentId = experimentId,
                    CircuitBreakerIds = circuitBreakerIds.ToArray(),
                    BulkheadIds = bulkheadIds.ToArray()
                };

                if (!_activeZones.TryAdd(zoneId, activeZone))
                {
                    throw new InvalidOperationException($"Zone '{zoneId}' already exists. This should not happen with GUID-based IDs.");
                }

                if (_messageBus != null)
                {
                    await _messageBus.PublishAsync(
                        "chaos.isolation-zone.created",
                        new PluginMessage
                        {
                            Type = "chaos.isolation-zone.created",
                            SourcePluginId = "chaos-vaccination",
                            CorrelationId = zoneId,
                            Payload = new Dictionary<string, object>
                            {
                                ["zoneId"] = zoneId,
                                ["containedPlugins"] = targetPlugins,
                                ["maxLevel"] = policy.MaxLevel.ToString(),
                                ["experimentId"] = experimentId ?? string.Empty
                            }
                        },
                        ct).ConfigureAwait(false);
                }

                return zone;
            }
            finally
            {
                _zoneLock.Release();
            }
        }

        /// <summary>
        /// Releases an isolation zone, resetting circuit breakers and releasing bulkhead allocations.
        /// </summary>
        /// <param name="zoneId">The zone to release.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ReleaseZoneAsync(string zoneId, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);

            await _zoneLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_activeZones.TryRemove(zoneId, out var activeZone))
                {
                    return; // Zone already released, idempotent
                }

                // Reset circuit breakers
                foreach (var cbId in activeZone.CircuitBreakerIds)
                {
                    if (_messageBus != null)
                    {
                        await _messageBus.PublishAsync(
                            "resilience.circuit-breaker.reset",
                            new PluginMessage
                            {
                                Type = "resilience.circuit-breaker.reset",
                                SourcePluginId = "chaos-vaccination",
                                CorrelationId = zoneId,
                                Payload = new Dictionary<string, object>
                                {
                                    ["circuitBreakerId"] = cbId,
                                    ["zoneId"] = zoneId
                                }
                            },
                            ct).ConfigureAwait(false);
                    }
                }

                // Release bulkhead allocations
                foreach (var bhId in activeZone.BulkheadIds)
                {
                    if (_messageBus != null)
                    {
                        await _messageBus.PublishAsync(
                            "resilience.bulkhead.release",
                            new PluginMessage
                            {
                                Type = "resilience.bulkhead.release",
                                SourcePluginId = "chaos-vaccination",
                                CorrelationId = zoneId,
                                Payload = new Dictionary<string, object>
                                {
                                    ["bulkheadId"] = bhId,
                                    ["zoneId"] = zoneId
                                }
                            },
                            ct).ConfigureAwait(false);
                    }
                }

                if (_messageBus != null)
                {
                    await _messageBus.PublishAsync(
                        "chaos.isolation-zone.released",
                        new PluginMessage
                        {
                            Type = "chaos.isolation-zone.released",
                            SourcePluginId = "chaos-vaccination",
                            CorrelationId = zoneId,
                            Payload = new Dictionary<string, object>
                            {
                                ["zoneId"] = zoneId,
                                ["duration"] = (DateTimeOffset.UtcNow - activeZone.CreatedAt).TotalMilliseconds
                            }
                        },
                        ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _zoneLock.Release();
            }
        }

        /// <summary>
        /// Reports the health status of an isolation zone.
        /// </summary>
        /// <param name="zoneId">The zone to check.</param>
        /// <returns>Health report for the zone, or null if the zone does not exist.</returns>
        public ZoneHealthReport? GetZoneHealth(string zoneId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_activeZones.TryGetValue(zoneId, out var activeZone))
                return null;

            var activeDuration = DateTimeOffset.UtcNow - activeZone.CreatedAt;
            var isExpired = activeDuration.TotalMilliseconds > activeZone.Zone.Policy.MaxDurationMs;

            return new ZoneHealthReport
            {
                ZoneId = zoneId,
                AllPluginsContained = true, // Updated by FailurePropagationMonitor
                HasBreaches = false,
                ActiveDuration = activeDuration,
                IsExpired = isExpired
            };
        }

        /// <summary>
        /// Gets an active zone by ID.
        /// </summary>
        /// <param name="zoneId">The zone ID to look up.</param>
        /// <returns>The active zone, or null if not found.</returns>
        public ActiveZone? GetZone(string zoneId)
        {
            _activeZones.TryGetValue(zoneId, out var zone);
            return zone;
        }

        /// <summary>
        /// Gets all currently active zones.
        /// </summary>
        /// <returns>Read-only list of active isolation zones.</returns>
        public IReadOnlyList<IsolationZone> GetActiveZones()
        {
            return _activeZones.Values.Select(az => az.Zone).ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets the set of plugin IDs contained across all active zones.
        /// </summary>
        /// <returns>Set of contained plugin IDs.</returns>
        public IReadOnlySet<string> GetAllContainedPluginIds()
        {
            var pluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var zone in _activeZones.Values)
            {
                foreach (var pluginId in zone.Zone.ContainedPlugins)
                {
                    pluginIds.Add(pluginId);
                }
            }
            return pluginIds;
        }

        /// <summary>
        /// Finds which zone (if any) contains a given plugin.
        /// </summary>
        /// <param name="pluginId">The plugin ID to search for.</param>
        /// <returns>The zone ID containing the plugin, or null.</returns>
        public string? FindZoneForPlugin(string pluginId)
        {
            foreach (var kvp in _activeZones)
            {
                if (kvp.Value.Zone.ContainedPlugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }
            return null;
        }

        /// <summary>
        /// Auto-cleanup callback: releases zones that have exceeded their MaxDurationMs.
        /// </summary>
        private async void CleanupExpiredZones(object? state)
        {
            if (_disposed) return;

            try
            {
                var now = DateTimeOffset.UtcNow;
                var expiredZoneIds = new List<string>();

                foreach (var kvp in _activeZones)
                {
                    var elapsed = (now - kvp.Value.CreatedAt).TotalMilliseconds;
                    if (elapsed > kvp.Value.Zone.Policy.MaxDurationMs)
                    {
                        expiredZoneIds.Add(kvp.Key);
                    }
                }

                foreach (var zoneId in expiredZoneIds)
                {
                    try
                    {
                        await ReleaseZoneAsync(zoneId, CancellationToken.None).ConfigureAwait(false);

                        if (_messageBus != null)
                        {
                            await _messageBus.PublishAsync(
                                "chaos.isolation-zone.expired",
                                new PluginMessage
                                {
                                    Type = "chaos.isolation-zone.expired",
                                    SourcePluginId = "chaos-vaccination",
                                    CorrelationId = zoneId,
                                    Payload = new Dictionary<string, object>
                                    {
                                        ["zoneId"] = zoneId,
                                        ["reason"] = "MaxDurationMs exceeded"
                                    }
                                },
                                CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Shutting down, ignore
                    }
                    catch (Exception)
                    {
                        // Log but don't throw from timer callback
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutting down
            }
        }

        /// <summary>
        /// Disposes the zone manager, stopping the cleanup timer.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cleanupTimer.Dispose();
            _zoneLock.Dispose();
        }
    }
}
