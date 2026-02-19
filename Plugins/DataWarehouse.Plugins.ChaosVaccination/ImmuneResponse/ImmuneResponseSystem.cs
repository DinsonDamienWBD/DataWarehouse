using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.ImmuneResponse;

/// <summary>
/// Implements the immune response system: a biological-metaphor subsystem that remembers
/// previously-seen faults and applies automatic remediation when those faults recur.
///
/// Core lifecycle:
/// 1. LEARN: Chaos experiments (or production incidents) feed fault signatures + successful
///    remediation actions into immune memory via <see cref="LearnFromExperimentAsync"/>.
/// 2. RECOGNIZE: When a fault occurs, <see cref="RecognizeFaultAsync"/> checks immune memory
///    for matching signatures using exact + fuzzy matching.
/// 3. REMEDIATE: If a match is found, <see cref="ApplyRemediationAsync"/> executes the learned
///    remediation actions through the message bus.
///
/// Memory management:
/// - Max 10,000 entries (configurable); LRU eviction by LastApplied when full.
/// - Entries not applied in 90 days have their SuccessRate halved (decay).
/// - Memory changes are broadcast via "chaos.immune-memory.changed" for cross-node sync.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Immune response system")]
public sealed class ImmuneResponseSystem : IImmuneResponseSystem
{
    private readonly IMessageBus? _messageBus;
    private readonly FaultSignatureAnalyzer _signatureAnalyzer;
    private readonly RemediationExecutor _remediationExecutor;
    private readonly ConcurrentDictionary<string, ImmuneMemoryEntry> _memory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _memoryLock = new(1, 1);

    /// <summary>Maximum number of immune memory entries before LRU eviction kicks in.</summary>
    private readonly int _maxMemoryEntries;

    /// <summary>Number of days after which unused entries have their success rate decayed.</summary>
    private const int DecayDays = 90;

    /// <summary>Topic used to broadcast immune memory changes for cross-node synchronization.</summary>
    private const string MemoryChangedTopic = "chaos.immune-memory.changed";

    /// <inheritdoc />
    public event Action<ImmuneResponseEvent>? OnImmuneResponse;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImmuneResponseSystem"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for remediation dispatch and memory sync. May be null.</param>
    /// <param name="signatureAnalyzer">Analyzer for generating and matching fault signatures.</param>
    /// <param name="maxMemoryEntries">Maximum immune memory capacity (default: 10000).</param>
    public ImmuneResponseSystem(
        IMessageBus? messageBus,
        FaultSignatureAnalyzer signatureAnalyzer,
        int maxMemoryEntries = 10000)
    {
        ArgumentNullException.ThrowIfNull(signatureAnalyzer);

        _messageBus = messageBus;
        _signatureAnalyzer = signatureAnalyzer;
        _remediationExecutor = new RemediationExecutor(messageBus);
        _maxMemoryEntries = maxMemoryEntries > 0 ? maxMemoryEntries : 10000;
    }

    /// <inheritdoc />
    public Task<ImmuneMemoryEntry?> RecognizeFaultAsync(FaultSignature signature, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ct.ThrowIfCancellationRequested();

        // Apply memory decay to all entries before matching
        ApplyMemoryDecay();

        var memoryEntries = _memory.Values.ToList();
        var match = _signatureAnalyzer.MatchSignature(signature, memoryEntries);

        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public async Task<bool> ApplyRemediationAsync(ImmuneMemoryEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        bool success = false;

        try
        {
            var result = await _remediationExecutor.ExecuteAsync(entry.RemediationActions, ct).ConfigureAwait(false);
            success = result.FailedActions == 0 && result.TotalActions > 0;

            stopwatch.Stop();
            var recoveryMs = stopwatch.ElapsedMilliseconds;

            // Update the entry statistics in memory
            UpdateEntryAfterApplication(entry.Signature.Hash, success, recoveryMs);

            // Fire immune response event
            RaiseImmuneResponseEvent(new ImmuneResponseEvent
            {
                SignatureHash = entry.Signature.Hash,
                ActionTaken = $"remediation:{result.SuccessfulActions}/{result.TotalActions} actions succeeded",
                Success = success,
                RecoveryTimeMs = recoveryMs,
                Timestamp = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            stopwatch.Stop();

            RaiseImmuneResponseEvent(new ImmuneResponseEvent
            {
                SignatureHash = entry.Signature.Hash,
                ActionTaken = "remediation:failed",
                Success = false,
                RecoveryTimeMs = stopwatch.ElapsedMilliseconds,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        return success;
    }

    /// <inheritdoc />
    public async Task LearnFromExperimentAsync(ChaosExperimentResult result, RemediationAction[] successfulActions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(successfulActions);
        ct.ThrowIfCancellationRequested();

        var signature = _signatureAnalyzer.GenerateSignature(result);

        await _memoryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_memory.TryGetValue(signature.Hash, out var existing))
            {
                // Update existing entry: increment observation count, merge remediation actions, update success rate
                var mergedActions = MergeRemediationActions(existing.RemediationActions, successfulActions);

                // Exponential moving average for success rate (alpha = 0.3 for responsiveness)
                const double alpha = 0.3;
                var newObservation = successfulActions.Length > 0 ? 1.0 : 0.0;
                var updatedSuccessRate = (alpha * newObservation) + ((1.0 - alpha) * existing.SuccessRate);

                var updatedSignature = signature with
                {
                    ObservationCount = existing.Signature.ObservationCount + 1,
                    FirstObserved = existing.Signature.FirstObserved // Preserve original observation time
                };

                var updatedEntry = existing with
                {
                    Signature = updatedSignature,
                    RemediationActions = mergedActions,
                    SuccessRate = updatedSuccessRate
                };

                _memory[signature.Hash] = updatedEntry;
            }
            else
            {
                // Evict LRU entries if at capacity
                EvictIfNecessary();

                var newEntry = new ImmuneMemoryEntry
                {
                    Signature = signature,
                    RemediationActions = successfulActions,
                    SuccessRate = successfulActions.Length > 0 ? 1.0 : 0.0,
                    LastApplied = null,
                    TimesApplied = 0,
                    AverageRecoveryMs = 0.0
                };

                _memory[signature.Hash] = newEntry;
            }
        }
        finally
        {
            _memoryLock.Release();
        }

        // Fire event
        RaiseImmuneResponseEvent(new ImmuneResponseEvent
        {
            SignatureHash = signature.Hash,
            ActionTaken = "learned",
            Success = true,
            RecoveryTimeMs = null,
            Timestamp = DateTimeOffset.UtcNow
        });

        // Broadcast memory change for cross-node sync
        await PublishMemoryChangeAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ImmuneMemoryEntry>> GetImmuneMemoryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<ImmuneMemoryEntry> entries = _memory.Values.ToList().AsReadOnly();
        return Task.FromResult(entries);
    }

    /// <inheritdoc />
    public async Task ForgetAsync(string signatureHash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signatureHash);
        ct.ThrowIfCancellationRequested();

        _memory.TryRemove(signatureHash, out _);

        // Broadcast memory change for cross-node sync
        await PublishMemoryChangeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates an immune memory entry after remediation has been applied.
    /// Increments TimesApplied, updates LastApplied, and recalculates AverageRecoveryMs.
    /// </summary>
    private void UpdateEntryAfterApplication(string signatureHash, bool success, long recoveryMs)
    {
        if (!_memory.TryGetValue(signatureHash, out var entry))
            return;

        var newTimesApplied = entry.TimesApplied + 1;

        // Incremental average: newAvg = oldAvg + (newValue - oldAvg) / newCount
        var newAverageRecoveryMs = entry.AverageRecoveryMs +
                                   ((recoveryMs - entry.AverageRecoveryMs) / newTimesApplied);

        // Update success rate with exponential moving average
        const double alpha = 0.3;
        var observation = success ? 1.0 : 0.0;
        var newSuccessRate = (alpha * observation) + ((1.0 - alpha) * entry.SuccessRate);

        var updatedEntry = entry with
        {
            TimesApplied = newTimesApplied,
            LastApplied = DateTimeOffset.UtcNow,
            AverageRecoveryMs = newAverageRecoveryMs,
            SuccessRate = newSuccessRate
        };

        _memory[signatureHash] = updatedEntry;
    }

    /// <summary>
    /// Applies memory decay: entries not applied in <see cref="DecayDays"/> days have
    /// their SuccessRate halved. This prevents stale remediation strategies from being
    /// applied with high confidence.
    /// </summary>
    private void ApplyMemoryDecay()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-DecayDays);

        foreach (var kvp in _memory)
        {
            var entry = kvp.Value;

            // Decay applies to entries that have been applied at least once but not recently,
            // or entries that were never applied and are older than the decay window.
            bool shouldDecay;
            if (entry.LastApplied.HasValue)
            {
                shouldDecay = entry.LastApplied.Value < cutoff;
            }
            else
            {
                shouldDecay = entry.Signature.FirstObserved < cutoff;
            }

            if (shouldDecay && entry.SuccessRate > 0.01) // Don't decay near-zero rates
            {
                var decayedEntry = entry with
                {
                    SuccessRate = entry.SuccessRate / 2.0
                };
                _memory.TryUpdate(kvp.Key, decayedEntry, entry);
            }
        }
    }

    /// <summary>
    /// Evicts the least-recently-used entry if memory is at capacity.
    /// LRU is determined by LastApplied (entries never applied use FirstObserved).
    /// </summary>
    private void EvictIfNecessary()
    {
        while (_memory.Count >= _maxMemoryEntries)
        {
            var lruKey = _memory
                .OrderBy(kvp =>
                    kvp.Value.LastApplied ?? kvp.Value.Signature.FirstObserved)
                .Select(kvp => kvp.Key)
                .FirstOrDefault();

            if (lruKey != null)
            {
                _memory.TryRemove(lruKey, out _);
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    /// Merges two sets of remediation actions, deduplicating by (ActionType, TargetId).
    /// If both sets contain the same action, the newer version (from incoming) wins.
    /// Result is sorted by Priority (lowest first).
    /// </summary>
    private static RemediationAction[] MergeRemediationActions(
        RemediationAction[] existing,
        RemediationAction[] incoming)
    {
        var merged = new Dictionary<string, RemediationAction>(StringComparer.Ordinal);

        foreach (var action in existing)
        {
            var key = $"{action.ActionType}:{action.TargetId}";
            merged[key] = action;
        }

        foreach (var action in incoming)
        {
            var key = $"{action.ActionType}:{action.TargetId}";
            merged[key] = action; // Incoming overwrites existing
        }

        return merged.Values.OrderBy(a => a.Priority).ToArray();
    }

    /// <summary>
    /// Publishes the current immune memory state to the message bus for cross-node synchronization.
    /// </summary>
    private async Task PublishMemoryChangeAsync(CancellationToken ct)
    {
        if (_messageBus == null)
            return;

        try
        {
            var entries = _memory.Values.ToList();
            var serialized = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });

            var message = new PluginMessage
            {
                Type = "immune-memory.changed",
                SourcePluginId = "chaos-vaccination",
                Payload = new Dictionary<string, object>
                {
                    ["entryCount"] = entries.Count,
                    ["serializedMemory"] = serialized,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
                }
            };

            await _messageBus.PublishAsync(MemoryChangedTopic, message, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Memory sync is best-effort; failure should not break the immune system
        }
    }

    /// <summary>
    /// Raises the <see cref="OnImmuneResponse"/> event safely.
    /// </summary>
    private void RaiseImmuneResponseEvent(ImmuneResponseEvent evt)
    {
        try
        {
            OnImmuneResponse?.Invoke(evt);
        }
        catch (Exception)
        {
            // Event handlers should not crash the immune system
        }
    }
}
