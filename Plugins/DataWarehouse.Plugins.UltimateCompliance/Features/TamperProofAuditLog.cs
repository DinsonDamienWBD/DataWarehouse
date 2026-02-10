using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateCompliance.Features
{
    /// <summary>
    /// Tamper-proof audit trail using hash chains and merkle trees for
    /// tamper detection and verification.
    /// </summary>
    public sealed class TamperProofAuditLog
    {
        private readonly List<AuditEntry> _entries = new();
        private string _previousHash = "0000000000000000000000000000000000000000000000000000000000000000";

        /// <summary>
        /// Appends an audit entry to the log.
        /// </summary>
        public string AppendEntry(string eventType, string userId, string resourceId, Dictionary<string, object> metadata)
        {
            var entry = new AuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                EventType = eventType,
                UserId = userId,
                ResourceId = resourceId,
                Timestamp = DateTime.UtcNow,
                Metadata = metadata,
                PreviousHash = _previousHash
            };

            entry.Hash = ComputeEntryHash(entry);
            _previousHash = entry.Hash;

            _entries.Add(entry);
            return entry.EntryId;
        }

        /// <summary>
        /// Verifies the integrity of the entire audit log.
        /// </summary>
        public AuditVerificationResult VerifyIntegrity()
        {
            if (_entries.Count == 0)
            {
                return new AuditVerificationResult
                {
                    IsValid = true,
                    Message = "Audit log is empty",
                    VerifiedEntries = 0
                };
            }

            var expectedPreviousHash = "0000000000000000000000000000000000000000000000000000000000000000";

            for (int i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];

                if (entry.PreviousHash != expectedPreviousHash)
                {
                    return new AuditVerificationResult
                    {
                        IsValid = false,
                        Message = $"Chain broken at entry {i}: expected previous hash {expectedPreviousHash}, found {entry.PreviousHash}",
                        VerifiedEntries = i,
                        TamperedEntryId = entry.EntryId
                    };
                }

                var computedHash = ComputeEntryHash(entry);
                if (computedHash != entry.Hash)
                {
                    return new AuditVerificationResult
                    {
                        IsValid = false,
                        Message = $"Hash mismatch at entry {i}: expected {entry.Hash}, computed {computedHash}",
                        VerifiedEntries = i,
                        TamperedEntryId = entry.EntryId
                    };
                }

                expectedPreviousHash = entry.Hash;
            }

            return new AuditVerificationResult
            {
                IsValid = true,
                Message = "All entries verified successfully",
                VerifiedEntries = _entries.Count
            };
        }

        /// <summary>
        /// Computes a Merkle root hash for a batch of entries.
        /// </summary>
        public string ComputeMerkleRoot(int startIndex = 0, int count = -1)
        {
            if (_entries.Count == 0)
                return string.Empty;

            count = count == -1 ? _entries.Count - startIndex : count;
            var batch = _entries.Skip(startIndex).Take(count).ToList();

            if (batch.Count == 0)
                return string.Empty;

            var hashes = batch.Select(e => e.Hash).ToList();
            return BuildMerkleTree(hashes);
        }

        /// <summary>
        /// Gets an audit entry by ID.
        /// </summary>
        public AuditEntry? GetEntry(string entryId)
        {
            return _entries.FirstOrDefault(e => e.EntryId == entryId);
        }

        /// <summary>
        /// Gets audit entries within a time range.
        /// </summary>
        public IReadOnlyList<AuditEntry> GetEntriesInRange(DateTime startTime, DateTime endTime)
        {
            return _entries
                .Where(e => e.Timestamp >= startTime && e.Timestamp <= endTime)
                .ToList();
        }

        /// <summary>
        /// Gets the total number of audit entries.
        /// </summary>
        public int EntryCount => _entries.Count;

        private static string ComputeEntryHash(AuditEntry entry)
        {
            var data = $"{entry.EntryId}|{entry.EventType}|{entry.UserId}|{entry.ResourceId}|{entry.Timestamp:O}|{entry.PreviousHash}|{JsonSerializer.Serialize(entry.Metadata)}";

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private static string BuildMerkleTree(List<string> hashes)
        {
            if (hashes.Count == 0)
                return string.Empty;

            if (hashes.Count == 1)
                return hashes[0];

            var nextLevel = new List<string>();

            for (int i = 0; i < hashes.Count; i += 2)
            {
                var left = hashes[i];
                var right = i + 1 < hashes.Count ? hashes[i + 1] : left;

                using var sha256 = SHA256.Create();
                var combined = left + right;
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
                nextLevel.Add(Convert.ToHexString(hashBytes).ToLowerInvariant());
            }

            return BuildMerkleTree(nextLevel);
        }
    }

    /// <summary>
    /// Represents a tamper-proof audit log entry.
    /// </summary>
    public sealed class AuditEntry
    {
        public required string EntryId { get; init; }
        public required string EventType { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required Dictionary<string, object> Metadata { get; init; }
        public required string PreviousHash { get; init; }
        public string Hash { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of audit log integrity verification.
    /// </summary>
    public sealed class AuditVerificationResult
    {
        public required bool IsValid { get; init; }
        public required string Message { get; init; }
        public required int VerifiedEntries { get; init; }
        public string? TamperedEntryId { get; init; }
    }
}
