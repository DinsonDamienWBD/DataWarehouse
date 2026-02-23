using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Authority
{
    /// <summary>
    /// Thread-safe store for immutable <see cref="EscalationRecord"/> instances.
    /// Records are keyed by a composite key of EscalationId and State, allowing multiple
    /// state transition records per escalation while preserving immutability of each record.
    /// <para>
    /// Every record stored must pass <see cref="EscalationRecord.VerifyIntegrity"/> validation.
    /// Tampered records are rejected with an <see cref="InvalidOperationException"/>.
    /// </para>
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 75: Emergency Escalation (AUTH-01, AUTH-02, AUTH-03)")]
    public sealed class EscalationRecordStore
    {
        // Composite key: "{EscalationId}:{State}" to allow one record per state transition
        private readonly ConcurrentDictionary<string, EscalationRecord> _records = new(StringComparer.Ordinal);

        // Index: EscalationId -> ordered list of state transition records
        private readonly ConcurrentDictionary<string, List<EscalationRecord>> _history = new(StringComparer.Ordinal);
        private readonly object _historyLock = new();

        /// <summary>
        /// Stores an <see cref="EscalationRecord"/> after verifying its integrity hash.
        /// Records are keyed by the composite of EscalationId and State, allowing one record
        /// per state transition per escalation.
        /// </summary>
        /// <param name="record">The escalation record to store.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="record"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the record's hash is invalid (tamper detection) or when a record with the
        /// same EscalationId and State already exists (immutability enforcement).
        /// </exception>
        public void Store(EscalationRecord record)
        {
            if (record is null)
                throw new ArgumentNullException(nameof(record));

            if (!record.VerifyIntegrity())
                throw new InvalidOperationException("Tamper detected: EscalationRecord hash is invalid");

            var compositeKey = FormatCompositeKey(record.EscalationId, record.State);

            if (!_records.TryAdd(compositeKey, record))
            {
                throw new InvalidOperationException(
                    $"EscalationRecord is immutable and cannot be replaced; " +
                    $"a record for escalation '{record.EscalationId}' in state '{record.State}' already exists");
            }

            // Add to the history index
            lock (_historyLock)
            {
                var list = _history.GetOrAdd(record.EscalationId, _ => new List<EscalationRecord>());
                list.Add(record);
            }
        }

        /// <summary>
        /// Retrieves the most recent state record for the specified escalation.
        /// The most recent record is determined by the highest <see cref="EscalationState"/> enum value
        /// (reflecting chronological progression through the state machine).
        /// </summary>
        /// <param name="escalationId">The escalation identifier.</param>
        /// <returns>The most recent <see cref="EscalationRecord"/>, or null if not found.</returns>
        public EscalationRecord? Get(string escalationId)
        {
            if (string.IsNullOrEmpty(escalationId))
                return null;

            lock (_historyLock)
            {
                if (!_history.TryGetValue(escalationId, out var list) || list.Count == 0)
                    return null;

                // Return the record with the highest state enum value (most recent transition)
                EscalationRecord? latest = null;
                for (var i = 0; i < list.Count; i++)
                {
                    if (latest is null || list[i].State > latest.State)
                        latest = list[i];
                }
                return latest;
            }
        }

        /// <summary>
        /// Returns all escalation records currently in the specified state.
        /// For Active state, this returns all escalations with Active as their most recent state.
        /// </summary>
        /// <param name="state">The state to filter by.</param>
        /// <returns>Read-only list of records whose most recent state matches the specified state.</returns>
        public IReadOnlyList<EscalationRecord> GetByState(EscalationState state)
        {
            var result = new List<EscalationRecord>();

            lock (_historyLock)
            {
                foreach (var kvp in _history)
                {
                    if (kvp.Value.Count == 0)
                        continue;

                    // Find the most recent state for this escalation
                    EscalationRecord? latest = null;
                    for (var i = 0; i < kvp.Value.Count; i++)
                    {
                        if (latest is null || kvp.Value[i].State > latest.State)
                            latest = kvp.Value[i];
                    }

                    if (latest is not null && latest.State == state)
                        result.Add(latest);
                }
            }

            return result.AsReadOnly();
        }

        /// <summary>
        /// Returns the complete state transition history for an escalation, ordered chronologically
        /// by state enum value (Pending first, terminal state last).
        /// </summary>
        /// <param name="escalationId">The escalation identifier.</param>
        /// <returns>
        /// Read-only list of all state transition records, ordered by state ascending.
        /// Returns an empty list if no records exist for the specified escalation.
        /// </returns>
        public IReadOnlyList<EscalationRecord> GetHistory(string escalationId)
        {
            if (string.IsNullOrEmpty(escalationId))
                return Array.Empty<EscalationRecord>();

            lock (_historyLock)
            {
                if (!_history.TryGetValue(escalationId, out var list) || list.Count == 0)
                    return Array.Empty<EscalationRecord>();

                return list.OrderBy(r => r.State).ThenBy(r => r.RequestedAt).ToList().AsReadOnly();
            }
        }

        /// <summary>
        /// Verifies the integrity of all records in an escalation's history chain.
        /// Returns true only if every record passes <see cref="EscalationRecord.VerifyIntegrity"/>.
        /// </summary>
        /// <param name="escalationId">The escalation identifier.</param>
        /// <returns>
        /// <c>true</c> if all records for this escalation have valid hashes;
        /// <c>false</c> if any record has been tampered with or if no records exist.
        /// </returns>
        public bool VerifyChainIntegrity(string escalationId)
        {
            if (string.IsNullOrEmpty(escalationId))
                return false;

            lock (_historyLock)
            {
                if (!_history.TryGetValue(escalationId, out var list) || list.Count == 0)
                    return false;

                for (var i = 0; i < list.Count; i++)
                {
                    if (!list[i].VerifyIntegrity())
                        return false;
                }

                return true;
            }
        }

        private static string FormatCompositeKey(string escalationId, EscalationState state)
        {
            return $"{escalationId}:{(int)state}";
        }
    }
}
