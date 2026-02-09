// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Interface for audit trail and provenance chain management.
/// Provides comprehensive tracking of all operations with cryptographic chain verification.
/// </summary>
public interface IAuditTrailService
{
    /// <summary>
    /// Logs an operation to the audit trail with hash chain linking.
    /// </summary>
    /// <param name="operation">The operation to log.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry with computed hash.</returns>
    Task<TamperProofAuditEntry> LogOperationAsync(AuditOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Gets the complete audit trail for a specific block/object.
    /// </summary>
    /// <param name="blockId">The block/object ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chronologically ordered list of audit entries.</returns>
    Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Gets audit entries across all objects within a time range.
    /// </summary>
    /// <param name="from">Start of time range.</param>
    /// <param name="to">End of time range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Audit entries within the time range.</returns>
    Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailByTimeRangeAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct = default);

    /// <summary>
    /// Gets audit entries filtered by operation type.
    /// </summary>
    /// <param name="operationType">The operation type to filter by.</param>
    /// <param name="from">Optional start of time range.</param>
    /// <param name="to">Optional end of time range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filtered audit entries.</returns>
    Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailByOperationTypeAsync(
        AuditOperationType operationType,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default);

    /// <summary>
    /// Builds the complete provenance chain for an object.
    /// Shows full data lineage from creation through all modifications.
    /// </summary>
    /// <param name="blockId">The block/object ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete provenance chain with validation status.</returns>
    Task<ProvenanceChain> GetProvenanceChainAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Verifies the cryptographic integrity of the provenance chain.
    /// Ensures no entries have been tampered with or removed.
    /// </summary>
    /// <param name="blockId">The block/object ID to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the chain is valid and unbroken.</returns>
    Task<bool> VerifyProvenanceChainAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Performs detailed verification of the provenance chain with diagnostics.
    /// </summary>
    /// <param name="blockId">The block/object ID to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Detailed verification result with any issues found.</returns>
    Task<ProvenanceChainVerificationResult> VerifyProvenanceChainDetailedAsync(
        Guid blockId,
        CancellationToken ct = default);

    /// <summary>
    /// Gets statistics about the audit trail.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Audit trail statistics.</returns>
    Task<AuditTrailStatistics> GetStatisticsAsync(CancellationToken ct = default);
}

/// <summary>
/// Implementation of audit trail and provenance chain management.
/// Uses cryptographic hash chaining for tamper-evident audit logging.
/// </summary>
public class AuditTrailService : IAuditTrailService
{
    private readonly ConcurrentDictionary<Guid, List<TamperProofAuditEntry>> _auditLog = new();
    private readonly ConcurrentDictionary<Guid, object> _locks = new();
    private readonly ILogger<AuditTrailService> _logger;
    private readonly TamperProofConfiguration _config;

    /// <summary>
    /// Creates a new audit trail service instance.
    /// </summary>
    /// <param name="config">Tamper-proof configuration.</param>
    /// <param name="logger">Logger instance.</param>
    public AuditTrailService(
        TamperProofConfiguration config,
        ILogger<AuditTrailService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<TamperProofAuditEntry> LogOperationAsync(AuditOperation operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var lockObj = _locks.GetOrAdd(operation.BlockId, _ => new object());

        lock (lockObj)
        {
            var entries = _auditLog.GetOrAdd(operation.BlockId, _ => new List<TamperProofAuditEntry>());

            // Get the previous entry's hash for chain linking
            var previousEntryHash = entries.Count > 0
                ? entries[^1].EntryHash
                : null;

            // Compute the hash for this entry
            var entryHash = ComputeEntryHash(
                operation.BlockId,
                operation.Type,
                DateTime.UtcNow,
                operation.UserId,
                operation.Details,
                previousEntryHash,
                operation.DataHash,
                operation.Version);

            var entry = new TamperProofAuditEntry(
                Id: Guid.NewGuid(),
                BlockId: operation.BlockId,
                Operation: operation.Type,
                Timestamp: DateTime.UtcNow,
                UserId: operation.UserId,
                Details: operation.Details,
                PreviousEntryHash: previousEntryHash,
                EntryHash: entryHash,
                DataHash: operation.DataHash,
                Version: operation.Version,
                Metadata: operation.Metadata);

            entries.Add(entry);

            _logger.LogDebug(
                "Audit entry logged: {Operation} for block {BlockId} by user {UserId}",
                operation.Type,
                operation.BlockId,
                operation.UserId ?? "system");

            return Task.FromResult(entry);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailAsync(Guid blockId, CancellationToken ct)
    {
        if (_auditLog.TryGetValue(blockId, out var entries))
        {
            var lockObj = _locks.GetOrAdd(blockId, _ => new object());
            lock (lockObj)
            {
                return Task.FromResult<IReadOnlyList<TamperProofAuditEntry>>(
                    entries.OrderBy(e => e.Timestamp).ToList());
            }
        }

        return Task.FromResult<IReadOnlyList<TamperProofAuditEntry>>(Array.Empty<TamperProofAuditEntry>());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailByTimeRangeAsync(
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        var results = new List<TamperProofAuditEntry>();

        foreach (var kvp in _auditLog)
        {
            var lockObj = _locks.GetOrAdd(kvp.Key, _ => new object());
            lock (lockObj)
            {
                results.AddRange(kvp.Value.Where(e =>
                    e.Timestamp >= from && e.Timestamp <= to));
            }
        }

        return Task.FromResult<IReadOnlyList<TamperProofAuditEntry>>(
            results.OrderBy(e => e.Timestamp).ToList());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TamperProofAuditEntry>> GetAuditTrailByOperationTypeAsync(
        AuditOperationType operationType,
        DateTime? from,
        DateTime? to,
        CancellationToken ct)
    {
        var results = new List<TamperProofAuditEntry>();

        foreach (var kvp in _auditLog)
        {
            var lockObj = _locks.GetOrAdd(kvp.Key, _ => new object());
            lock (lockObj)
            {
                var filtered = kvp.Value.Where(e => e.Operation == operationType);

                if (from.HasValue)
                    filtered = filtered.Where(e => e.Timestamp >= from.Value);

                if (to.HasValue)
                    filtered = filtered.Where(e => e.Timestamp <= to.Value);

                results.AddRange(filtered);
            }
        }

        return Task.FromResult<IReadOnlyList<TamperProofAuditEntry>>(
            results.OrderBy(e => e.Timestamp).ToList());
    }

    /// <inheritdoc/>
    public Task<ProvenanceChain> GetProvenanceChainAsync(Guid blockId, CancellationToken ct)
    {
        if (!_auditLog.TryGetValue(blockId, out var entries))
        {
            return Task.FromResult(new ProvenanceChain(
                BlockId: blockId,
                CreatedAt: null,
                OriginalHash: null,
                Entries: Array.Empty<TamperProofAuditEntry>(),
                IsChainValid: true,
                TotalOperations: 0,
                LastModifiedAt: null,
                LastModifiedBy: null,
                RecoveryCount: 0,
                CorruptionCount: 0));
        }

        var lockObj = _locks.GetOrAdd(blockId, _ => new object());
        lock (lockObj)
        {
            var orderedEntries = entries.OrderBy(e => e.Timestamp).ToList();

            // Find creation entry
            var creationEntry = orderedEntries.FirstOrDefault(e => e.Operation == AuditOperationType.Created);

            // Find last modification
            var lastModification = orderedEntries
                .Where(e => e.Operation == AuditOperationType.Modified ||
                           e.Operation == AuditOperationType.SecureCorrected)
                .OrderByDescending(e => e.Timestamp)
                .FirstOrDefault();

            // Count recoveries and corruption events
            var recoveryCount = orderedEntries.Count(e =>
                e.Operation == AuditOperationType.RecoverySucceeded);
            var corruptionCount = orderedEntries.Count(e =>
                e.Operation == AuditOperationType.CorruptionDetected);

            // Verify chain integrity
            var isChainValid = VerifyChainIntegrity(orderedEntries);

            return Task.FromResult(new ProvenanceChain(
                BlockId: blockId,
                CreatedAt: creationEntry?.Timestamp,
                OriginalHash: creationEntry?.DataHash,
                Entries: orderedEntries,
                IsChainValid: isChainValid,
                TotalOperations: orderedEntries.Count,
                LastModifiedAt: lastModification?.Timestamp,
                LastModifiedBy: lastModification?.UserId,
                RecoveryCount: recoveryCount,
                CorruptionCount: corruptionCount));
        }
    }

    /// <inheritdoc/>
    public Task<bool> VerifyProvenanceChainAsync(Guid blockId, CancellationToken ct)
    {
        if (!_auditLog.TryGetValue(blockId, out var entries))
        {
            // Empty chain is considered valid
            return Task.FromResult(true);
        }

        var lockObj = _locks.GetOrAdd(blockId, _ => new object());
        lock (lockObj)
        {
            var orderedEntries = entries.OrderBy(e => e.Timestamp).ToList();
            return Task.FromResult(VerifyChainIntegrity(orderedEntries));
        }
    }

    /// <inheritdoc/>
    public Task<ProvenanceChainVerificationResult> VerifyProvenanceChainDetailedAsync(
        Guid blockId,
        CancellationToken ct)
    {
        if (!_auditLog.TryGetValue(blockId, out var entries))
        {
            return Task.FromResult(new ProvenanceChainVerificationResult(
                BlockId: blockId,
                IsValid: true,
                TotalEntries: 0,
                ValidEntries: 0,
                InvalidEntries: 0,
                Issues: Array.Empty<ProvenanceChainIssue>(),
                VerifiedAt: DateTime.UtcNow));
        }

        var lockObj = _locks.GetOrAdd(blockId, _ => new object());
        lock (lockObj)
        {
            var orderedEntries = entries.OrderBy(e => e.Timestamp).ToList();
            var issues = new List<ProvenanceChainIssue>();
            var validCount = 0;
            var invalidCount = 0;

            for (int i = 0; i < orderedEntries.Count; i++)
            {
                var entry = orderedEntries[i];
                var expectedPreviousHash = i > 0 ? orderedEntries[i - 1].EntryHash : null;

                // Verify previous hash link
                if (entry.PreviousEntryHash != expectedPreviousHash)
                {
                    issues.Add(new ProvenanceChainIssue(
                        EntryId: entry.Id,
                        EntryIndex: i,
                        IssueType: ProvenanceChainIssueType.BrokenChainLink,
                        Description: $"Entry {i} has incorrect previous hash link",
                        ExpectedValue: expectedPreviousHash,
                        ActualValue: entry.PreviousEntryHash));
                    invalidCount++;
                    continue;
                }

                // Verify entry hash
                var recomputedHash = ComputeEntryHash(
                    entry.BlockId,
                    entry.Operation,
                    entry.Timestamp,
                    entry.UserId,
                    entry.Details,
                    entry.PreviousEntryHash,
                    entry.DataHash,
                    entry.Version);

                if (recomputedHash != entry.EntryHash)
                {
                    issues.Add(new ProvenanceChainIssue(
                        EntryId: entry.Id,
                        EntryIndex: i,
                        IssueType: ProvenanceChainIssueType.TamperedEntry,
                        Description: $"Entry {i} hash does not match computed hash",
                        ExpectedValue: entry.EntryHash,
                        ActualValue: recomputedHash));
                    invalidCount++;
                    continue;
                }

                // Verify chronological ordering
                if (i > 0 && entry.Timestamp < orderedEntries[i - 1].Timestamp)
                {
                    issues.Add(new ProvenanceChainIssue(
                        EntryId: entry.Id,
                        EntryIndex: i,
                        IssueType: ProvenanceChainIssueType.ChronologicalViolation,
                        Description: $"Entry {i} timestamp is before previous entry",
                        ExpectedValue: orderedEntries[i - 1].Timestamp.ToString("O"),
                        ActualValue: entry.Timestamp.ToString("O")));
                    invalidCount++;
                    continue;
                }

                validCount++;
            }

            return Task.FromResult(new ProvenanceChainVerificationResult(
                BlockId: blockId,
                IsValid: issues.Count == 0,
                TotalEntries: orderedEntries.Count,
                ValidEntries: validCount,
                InvalidEntries: invalidCount,
                Issues: issues,
                VerifiedAt: DateTime.UtcNow));
        }
    }

    /// <inheritdoc/>
    public Task<AuditTrailStatistics> GetStatisticsAsync(CancellationToken ct)
    {
        var totalEntries = 0;
        var entriesByOperation = new Dictionary<AuditOperationType, int>();
        var uniqueBlocks = _auditLog.Count;
        var oldestEntry = DateTime.MaxValue;
        var newestEntry = DateTime.MinValue;

        foreach (var kvp in _auditLog)
        {
            var lockObj = _locks.GetOrAdd(kvp.Key, _ => new object());
            lock (lockObj)
            {
                totalEntries += kvp.Value.Count;

                foreach (var entry in kvp.Value)
                {
                    if (!entriesByOperation.ContainsKey(entry.Operation))
                        entriesByOperation[entry.Operation] = 0;
                    entriesByOperation[entry.Operation]++;

                    if (entry.Timestamp < oldestEntry)
                        oldestEntry = entry.Timestamp;
                    if (entry.Timestamp > newestEntry)
                        newestEntry = entry.Timestamp;
                }
            }
        }

        return Task.FromResult(new AuditTrailStatistics(
            TotalEntries: totalEntries,
            UniqueBlocks: uniqueBlocks,
            EntriesByOperation: entriesByOperation,
            OldestEntry: totalEntries > 0 ? oldestEntry : null,
            NewestEntry: totalEntries > 0 ? newestEntry : null,
            AverageEntriesPerBlock: uniqueBlocks > 0 ? (double)totalEntries / uniqueBlocks : 0));
    }

    /// <summary>
    /// Verifies the integrity of a chain of entries.
    /// </summary>
    private bool VerifyChainIntegrity(List<TamperProofAuditEntry> orderedEntries)
    {
        for (int i = 0; i < orderedEntries.Count; i++)
        {
            var entry = orderedEntries[i];
            var expectedPreviousHash = i > 0 ? orderedEntries[i - 1].EntryHash : null;

            // Verify chain link
            if (entry.PreviousEntryHash != expectedPreviousHash)
            {
                _logger.LogWarning(
                    "Chain integrity violation at entry {Index} for block {BlockId}: broken link",
                    i, entry.BlockId);
                return false;
            }

            // Verify entry hash
            var recomputedHash = ComputeEntryHash(
                entry.BlockId,
                entry.Operation,
                entry.Timestamp,
                entry.UserId,
                entry.Details,
                entry.PreviousEntryHash,
                entry.DataHash,
                entry.Version);

            if (recomputedHash != entry.EntryHash)
            {
                _logger.LogWarning(
                    "Chain integrity violation at entry {Index} for block {BlockId}: tampered hash",
                    i, entry.BlockId);
                return false;
            }

            // Verify chronological ordering
            if (i > 0 && entry.Timestamp < orderedEntries[i - 1].Timestamp)
            {
                _logger.LogWarning(
                    "Chain integrity violation at entry {Index} for block {BlockId}: chronological violation",
                    i, entry.BlockId);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Computes the cryptographic hash for an audit entry.
    /// The hash includes all entry fields and the previous entry's hash for chain linking.
    /// </summary>
    private static string ComputeEntryHash(
        Guid blockId,
        AuditOperationType operation,
        DateTime timestamp,
        string? userId,
        string details,
        string? previousEntryHash,
        string? dataHash,
        int? version)
    {
        var data = new
        {
            BlockId = blockId,
            Operation = operation.ToString(),
            Timestamp = timestamp.ToString("O"),
            UserId = userId ?? string.Empty,
            Details = details,
            PreviousEntryHash = previousEntryHash ?? string.Empty,
            DataHash = dataHash ?? string.Empty,
            Version = version ?? 0
        };

        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// Represents a single entry in the audit trail.
/// Each entry is cryptographically linked to the previous entry.
/// </summary>
/// <param name="Id">Unique identifier for this audit entry.</param>
/// <param name="BlockId">The block/object ID this entry relates to.</param>
/// <param name="Operation">Type of operation performed.</param>
/// <param name="Timestamp">When the operation occurred.</param>
/// <param name="UserId">User who performed the operation (null for system operations).</param>
/// <param name="Details">Human-readable description of the operation.</param>
/// <param name="PreviousEntryHash">Hash of the previous entry in the chain (null for first entry).</param>
/// <param name="EntryHash">Cryptographic hash of this entry including all fields.</param>
/// <param name="DataHash">Hash of the data at this point (for data operations).</param>
/// <param name="Version">Version number of the data (for versioned operations).</param>
/// <param name="Metadata">Additional operation-specific metadata.</param>
public record TamperProofAuditEntry(
    Guid Id,
    Guid BlockId,
    AuditOperationType Operation,
    DateTime Timestamp,
    string? UserId,
    string Details,
    string? PreviousEntryHash,
    string EntryHash,
    string? DataHash = null,
    int? Version = null,
    Dictionary<string, string>? Metadata = null);

/// <summary>
/// Represents an operation to be logged to the audit trail.
/// </summary>
/// <param name="BlockId">The block/object ID this operation relates to.</param>
/// <param name="Type">Type of operation being performed.</param>
/// <param name="UserId">User performing the operation (null for system operations).</param>
/// <param name="Details">Human-readable description of the operation.</param>
/// <param name="DataHash">Hash of the data (for data operations).</param>
/// <param name="Version">Version number (for versioned operations).</param>
/// <param name="Metadata">Additional operation-specific metadata.</param>
public record AuditOperation(
    Guid BlockId,
    AuditOperationType Type,
    string? UserId,
    string Details,
    string? DataHash = null,
    int? Version = null,
    Dictionary<string, string>? Metadata = null);

/// <summary>
/// Types of operations that can be recorded in the audit trail.
/// </summary>
public enum AuditOperationType
{
    /// <summary>Initial creation of data.</summary>
    Created,

    /// <summary>Data was read/accessed.</summary>
    Read,

    /// <summary>Data was modified (new version).</summary>
    Modified,

    /// <summary>Data integrity was verified.</summary>
    Verified,

    /// <summary>Data corruption was detected.</summary>
    CorruptionDetected,

    /// <summary>Recovery was attempted.</summary>
    RecoveryAttempted,

    /// <summary>Recovery completed successfully.</summary>
    RecoverySucceeded,

    /// <summary>Recovery failed.</summary>
    RecoveryFailed,

    /// <summary>Data was sealed (made read-only).</summary>
    Sealed,

    /// <summary>Authorized correction was made.</summary>
    SecureCorrected,

    /// <summary>Data was deleted.</summary>
    Deleted,

    /// <summary>Blockchain anchor was created.</summary>
    BlockchainAnchored,

    /// <summary>WORM backup was created.</summary>
    WormBackupCreated,

    /// <summary>RAID shards were written.</summary>
    ShardsWritten,

    /// <summary>Manifest was updated.</summary>
    ManifestUpdated,

    /// <summary>Access permissions were changed.</summary>
    PermissionsChanged,

    /// <summary>Retention policy was applied.</summary>
    RetentionPolicyApplied,

    /// <summary>Legal hold was applied.</summary>
    LegalHoldApplied,

    /// <summary>Legal hold was released.</summary>
    LegalHoldReleased
}

/// <summary>
/// Represents the complete provenance chain for a block/object.
/// Shows full data lineage from creation through all modifications.
/// </summary>
/// <param name="BlockId">The block/object ID.</param>
/// <param name="CreatedAt">When the data was originally created.</param>
/// <param name="OriginalHash">Hash of the original data at creation.</param>
/// <param name="Entries">All audit entries in chronological order.</param>
/// <param name="IsChainValid">Whether the hash chain is valid and unbroken.</param>
/// <param name="TotalOperations">Total number of operations in the chain.</param>
/// <param name="LastModifiedAt">When the data was last modified.</param>
/// <param name="LastModifiedBy">Who last modified the data.</param>
/// <param name="RecoveryCount">Number of times data was recovered.</param>
/// <param name="CorruptionCount">Number of corruption events detected.</param>
public record ProvenanceChain(
    Guid BlockId,
    DateTime? CreatedAt,
    string? OriginalHash,
    IReadOnlyList<TamperProofAuditEntry> Entries,
    bool IsChainValid,
    int TotalOperations,
    DateTime? LastModifiedAt,
    string? LastModifiedBy,
    int RecoveryCount,
    int CorruptionCount);

/// <summary>
/// Detailed result of provenance chain verification.
/// </summary>
/// <param name="BlockId">The block/object ID that was verified.</param>
/// <param name="IsValid">Whether the chain passed all verification checks.</param>
/// <param name="TotalEntries">Total number of entries in the chain.</param>
/// <param name="ValidEntries">Number of entries that passed verification.</param>
/// <param name="InvalidEntries">Number of entries that failed verification.</param>
/// <param name="Issues">List of specific issues found.</param>
/// <param name="VerifiedAt">When the verification was performed.</param>
public record ProvenanceChainVerificationResult(
    Guid BlockId,
    bool IsValid,
    int TotalEntries,
    int ValidEntries,
    int InvalidEntries,
    IReadOnlyList<ProvenanceChainIssue> Issues,
    DateTime VerifiedAt);

/// <summary>
/// Describes a specific issue found during provenance chain verification.
/// </summary>
/// <param name="EntryId">ID of the entry with the issue.</param>
/// <param name="EntryIndex">Index of the entry in the chain.</param>
/// <param name="IssueType">Type of issue detected.</param>
/// <param name="Description">Human-readable description of the issue.</param>
/// <param name="ExpectedValue">Expected value (if applicable).</param>
/// <param name="ActualValue">Actual value found (if applicable).</param>
public record ProvenanceChainIssue(
    Guid EntryId,
    int EntryIndex,
    ProvenanceChainIssueType IssueType,
    string Description,
    string? ExpectedValue,
    string? ActualValue);

/// <summary>
/// Types of issues that can be detected during provenance chain verification.
/// </summary>
public enum ProvenanceChainIssueType
{
    /// <summary>The hash link to the previous entry is broken.</summary>
    BrokenChainLink,

    /// <summary>The entry's hash does not match the computed hash (tampering).</summary>
    TamperedEntry,

    /// <summary>Entry timestamp is before the previous entry.</summary>
    ChronologicalViolation,

    /// <summary>Entry is missing required fields.</summary>
    MissingRequiredField,

    /// <summary>Duplicate entry ID detected.</summary>
    DuplicateEntry,

    /// <summary>Unexpected gap in the chain.</summary>
    MissingEntry
}

/// <summary>
/// Statistics about the audit trail.
/// </summary>
/// <param name="TotalEntries">Total number of audit entries.</param>
/// <param name="UniqueBlocks">Number of unique blocks/objects tracked.</param>
/// <param name="EntriesByOperation">Breakdown of entries by operation type.</param>
/// <param name="OldestEntry">Timestamp of the oldest entry.</param>
/// <param name="NewestEntry">Timestamp of the newest entry.</param>
/// <param name="AverageEntriesPerBlock">Average number of entries per block.</param>
public record AuditTrailStatistics(
    int TotalEntries,
    int UniqueBlocks,
    Dictionary<AuditOperationType, int> EntriesByOperation,
    DateTime? OldestEntry,
    DateTime? NewestEntry,
    double AverageEntriesPerBlock);
