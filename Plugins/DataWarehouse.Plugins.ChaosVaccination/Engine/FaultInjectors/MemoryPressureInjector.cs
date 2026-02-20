using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.Engine.FaultInjectors;

/// <summary>
/// Fault injector that creates controlled memory pressure by allocating byte arrays.
/// Supports gradual and spike allocation patterns. Allocated memory is tracked and
/// released during cleanup, with a forced GC collect to reclaim the memory.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos injection engine")]
public sealed class MemoryPressureInjector : IFaultInjector
{
    private readonly IMessageBus? _messageBus;
    private readonly BoundedDictionary<string, MemoryPressureState> _activeAllocations = new BoundedDictionary<string, MemoryPressureState>(1000);

    /// <summary>
    /// Tracks the state of an active memory pressure injection.
    /// </summary>
    private sealed class MemoryPressureState
    {
        public List<byte[]> Allocations { get; } = new();
        public DateTimeOffset StartedAt { get; init; }
        public int TargetMB { get; init; }
        public long AllocatedBytes { get; set; }
        public long PeakWorkingSetBytes { get; init; }
    }

    /// <inheritdoc/>
    public FaultType SupportedFaultType => FaultType.MemoryPressure;

    /// <inheritdoc/>
    public string Name => "Memory Pressure Injector";

    /// <summary>
    /// Initializes a new MemoryPressureInjector with optional message bus.
    /// </summary>
    /// <param name="messageBus">Optional message bus for cluster-wide coordination.</param>
    public MemoryPressureInjector(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public async Task<FaultInjectionResult> InjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        try
        {
            var pressureMB = ExtractIntParameter(experiment, "pressureMB", 100);
            var allocationPattern = ExtractStringParameter(experiment, "allocationPattern", "gradual");

            // Cap at reasonable limit to prevent catastrophic OOM
            pressureMB = Math.Min(pressureMB, 2048);

            var process = Process.GetCurrentProcess();
            var peakWorkingSet = process.WorkingSet64;

            var state = new MemoryPressureState
            {
                StartedAt = DateTimeOffset.UtcNow,
                TargetMB = pressureMB,
                PeakWorkingSetBytes = peakWorkingSet
            };

            _activeAllocations[experiment.Id] = state;

            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("chaos.fault.memory-pressure", new PluginMessage
                {
                    Type = "chaos.fault.memory-pressure",
                    SourcePluginId = "com.datawarehouse.chaos.vaccination",
                    Payload = new Dictionary<string, object>
                    {
                        ["experimentId"] = experiment.Id,
                        ["pressureMB"] = pressureMB,
                        ["allocationPattern"] = allocationPattern,
                        ["severity"] = experiment.Severity.ToString(),
                        ["action"] = "inject"
                    }
                }, ct);
            }

            // Allocate memory in controlled chunks
            var bytesPerMB = 1024 * 1024;
            var totalBytes = (long)pressureMB * bytesPerMB;

            if (allocationPattern == "spike")
            {
                // Allocate all at once in a single large block
                AllocateChunk(state, totalBytes, ct);
            }
            else
            {
                // Gradual allocation in 1MB chunks with cancellation checks
                var chunkSize = bytesPerMB;
                var remaining = totalBytes;

                while (remaining > 0 && !ct.IsCancellationRequested)
                {
                    var currentChunk = Math.Min(chunkSize, remaining);
                    AllocateChunk(state, currentChunk, ct);
                    remaining -= currentChunk;
                }
            }

            var gcCollections = GC.CollectionCount(0);

            var signature = new FaultSignature
            {
                Hash = $"memory-pressure-{pressureMB}mb-{allocationPattern}",
                FaultType = FaultType.MemoryPressure,
                Pattern = $"Memory pressure {pressureMB}MB ({allocationPattern} pattern)",
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
                    ["allocatedMB"] = state.AllocatedBytes / (double)bytesPerMB,
                    ["peakWorkingSetMB"] = process.WorkingSet64 / (double)bytesPerMB,
                    ["gcCollections"] = gcCollections
                },
                CleanupRequired = true
            };
        }
        catch (OutOfMemoryException)
        {
            // Partial allocation succeeded before OOM -- still need cleanup
            return new FaultInjectionResult
            {
                Success = true, // Partial success -- we did create memory pressure
                ErrorMessage = "OutOfMemoryException during allocation -- partial pressure applied",
                AffectedComponents = experiment.TargetPluginIds ?? Array.Empty<string>(),
                Metrics = new Dictionary<string, double>
                {
                    ["allocatedMB"] = _activeAllocations.TryGetValue(experiment.Id, out var s)
                        ? s.AllocatedBytes / (1024.0 * 1024.0) : 0
                },
                CleanupRequired = true
            };
        }
        catch (Exception ex)
        {
            return new FaultInjectionResult
            {
                Success = false,
                ErrorMessage = $"Failed to inject memory pressure: {ex.Message}",
                CleanupRequired = _activeAllocations.ContainsKey(experiment.Id)
            };
        }
    }

    /// <inheritdoc/>
    public async Task CleanupAsync(string experimentId, CancellationToken ct)
    {
        if (!_activeAllocations.TryRemove(experimentId, out var state))
            return;

        // Release all allocated memory
        state.Allocations.Clear();

        // Force garbage collection to reclaim the memory
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        if (_messageBus != null)
        {
            await _messageBus.PublishAsync("chaos.fault.memory-pressure", new PluginMessage
            {
                Type = "chaos.fault.memory-pressure.cleared",
                SourcePluginId = "com.datawarehouse.chaos.vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["experimentId"] = experimentId,
                    ["action"] = "clear",
                    ["releasedMB"] = state.AllocatedBytes / (1024.0 * 1024.0),
                    ["durationMs"] = (DateTimeOffset.UtcNow - state.StartedAt).TotalMilliseconds
                }
            }, ct);
        }
    }

    /// <inheritdoc/>
    public Task<bool> CanInjectAsync(ChaosExperiment experiment, CancellationToken ct)
    {
        // Check that we have enough memory headroom for the requested pressure
        var pressureMB = ExtractIntParameter(experiment, "pressureMB", 100);
        var process = Process.GetCurrentProcess();
        var availableMemoryMB = (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes - process.WorkingSet64) / (1024.0 * 1024.0);

        // Only allow if we have at least 2x the requested pressure in available memory
        return Task.FromResult(availableMemoryMB >= pressureMB * 2);
    }

    private static void AllocateChunk(MemoryPressureState state, long bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Allocate in segments of at most 256MB to avoid LOH fragmentation issues
        const int maxSegment = 256 * 1024 * 1024;
        var remaining = bytes;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var segmentSize = (int)Math.Min(remaining, maxSegment);
            var block = new byte[segmentSize];

            // Touch the memory to ensure it is committed (not just reserved)
            for (var i = 0; i < segmentSize; i += 4096)
            {
                block[i] = 0xFF;
            }

            state.Allocations.Add(block);
            state.AllocatedBytes += segmentSize;
            remaining -= segmentSize;
        }
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
