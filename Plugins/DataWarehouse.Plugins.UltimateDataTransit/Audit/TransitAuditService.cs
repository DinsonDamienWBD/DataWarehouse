using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit.Audit;

/// <summary>
/// Provides structured audit trail logging for all data transit operations.
/// Maintains an in-memory audit log keyed by transfer ID and publishes each
/// audit entry to the message bus for cross-plugin consumption.
/// </summary>
/// <remarks>
/// <para>
/// Every transfer lifecycle event (start, complete, fail, resume, cancel) is logged
/// with full context including strategy used, endpoints, bytes transferred, duration,
/// and any additional details (layers applied, QoS tier, cost information).
/// </para>
/// <para>
/// Audit entries are published to the <see cref="TransitMessageTopics.AuditEntry"/> topic
/// on the message bus, enabling consumption by compliance, monitoring, analytics, and
/// other cross-plugin subscribers.
/// </para>
/// <para>
/// Thread-safe for concurrent audit logging from multiple transfer operations.
/// </para>
/// </remarks>
internal sealed class TransitAuditService
{
    private readonly IMessageBus _messageBus;
    private readonly ConcurrentDictionary<string, List<TransitAuditEntry>> _auditLog = new();
    private long _totalAuditEntries;

    /// <summary>
    /// Lock objects per transfer ID to synchronize list access within a single transfer.
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _transferLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TransitAuditService"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for publishing audit entries to subscribers.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="messageBus"/> is null.</exception>
    public TransitAuditService(IMessageBus messageBus)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        _messageBus = messageBus;
    }

    /// <summary>
    /// Logs an audit event and publishes it to the message bus for cross-plugin consumption.
    /// </summary>
    /// <param name="entry">The audit entry to log. Must have a non-null TransferId.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="entry"/> is null.</exception>
    public void LogEvent(TransitAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Add entry to the per-transfer audit log
        var lockObj = _transferLocks.GetOrAdd(entry.TransferId, _ => new object());
        lock (lockObj)
        {
            var entries = _auditLog.GetOrAdd(entry.TransferId, _ => new List<TransitAuditEntry>());
            entries.Add(entry);
        }

        Interlocked.Increment(ref _totalAuditEntries);

        // Publish to message bus asynchronously (fire-and-forget for audit)
        PublishAuditEntryAsync(entry);
    }

    /// <summary>
    /// Gets the complete audit trail for a specific transfer.
    /// </summary>
    /// <param name="transferId">The transfer identifier to query.</param>
    /// <returns>A read-only list of audit entries for the transfer, or an empty list if not found.</returns>
    public IReadOnlyList<TransitAuditEntry> GetAuditTrail(string transferId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);

        if (_auditLog.TryGetValue(transferId, out var entries))
        {
            var lockObj = _transferLocks.GetOrAdd(transferId, _ => new object());
            lock (lockObj)
            {
                return entries.ToList().AsReadOnly();
            }
        }

        return Array.Empty<TransitAuditEntry>();
    }

    /// <summary>
    /// Gets the most recent audit entries across all transfers, sorted by timestamp descending.
    /// </summary>
    /// <param name="count">The maximum number of entries to return.</param>
    /// <returns>A read-only list of the most recent audit entries.</returns>
    public IReadOnlyList<TransitAuditEntry> GetRecentEntries(int count)
    {
        if (count <= 0) return Array.Empty<TransitAuditEntry>();

        var allEntries = new List<TransitAuditEntry>();

        foreach (var kvp in _auditLog)
        {
            var lockObj = _transferLocks.GetOrAdd(kvp.Key, _ => new object());
            lock (lockObj)
            {
                allEntries.AddRange(kvp.Value);
            }
        }

        return allEntries
            .OrderByDescending(e => e.Timestamp)
            .Take(count)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Gets the total number of audit entries logged since service creation.
    /// </summary>
    /// <returns>The total audit entry count.</returns>
    public long GetTotalEntries()
    {
        return Interlocked.Read(ref _totalAuditEntries);
    }

    /// <summary>
    /// Purges audit entries older than the specified cutoff timestamp.
    /// Used for log rotation and memory management in long-running systems.
    /// </summary>
    /// <param name="cutoff">The UTC cutoff time. Entries older than this are removed.</param>
    public void PurgeOlderThan(DateTime cutoff)
    {
        foreach (var kvp in _auditLog)
        {
            var lockObj = _transferLocks.GetOrAdd(kvp.Key, _ => new object());
            lock (lockObj)
            {
                var removed = kvp.Value.RemoveAll(e => e.Timestamp < cutoff);
                if (removed > 0)
                {
                    Interlocked.Add(ref _totalAuditEntries, -removed);
                }

                // Clean up empty transfer logs
                if (kvp.Value.Count == 0)
                {
                    _auditLog.TryRemove(kvp.Key, out _);
                    _transferLocks.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    /// <summary>
    /// Publishes an audit entry to the message bus on the transit.audit.entry topic.
    /// Fire-and-forget: publish failures are silently ignored to avoid impacting transfer operations.
    /// </summary>
    /// <param name="entry">The audit entry to publish.</param>
    private void PublishAuditEntryAsync(TransitAuditEntry entry)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["auditId"] = entry.AuditId,
                    ["transferId"] = entry.TransferId,
                    ["eventType"] = entry.EventType.ToString(),
                    ["timestamp"] = entry.Timestamp.ToString("O"),
                    ["strategyId"] = entry.StrategyId,
                    ["sourceEndpoint"] = entry.SourceEndpoint,
                    ["destinationEndpoint"] = entry.DestinationEndpoint,
                    ["bytesTransferred"] = entry.BytesTransferred,
                    ["durationMs"] = entry.Duration.TotalMilliseconds,
                    ["success"] = entry.Success,
                };

                if (entry.ErrorMessage != null)
                {
                    payload["errorMessage"] = entry.ErrorMessage;
                }

                if (entry.UserId != null)
                {
                    payload["userId"] = entry.UserId;
                }

                if (entry.Details != null)
                {
                    payload["details"] = entry.Details;
                }

                var message = new PluginMessage
                {
                    Type = entry.EventType.ToString(),
                    SourcePluginId = "com.datawarehouse.transit.ultimate",
                    Payload = payload
                };

                await _messageBus.PublishAsync(TransitMessageTopics.AuditEntry, message);
            }
            catch
            {
                // Audit publish failures must not impact transfer operations
            }
        });
    }
}
