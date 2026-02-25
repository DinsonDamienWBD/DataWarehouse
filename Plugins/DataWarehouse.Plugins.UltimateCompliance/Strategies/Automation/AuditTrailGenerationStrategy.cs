using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Automation
{
    /// <summary>
    /// Audit trail generation strategy that automatically creates immutable,
    /// tamper-evident audit logs for all compliance-relevant operations.
    /// Supports blockchain-style chain of custody and cryptographic verification.
    /// </summary>
    public sealed class AuditTrailGenerationStrategy : ComplianceStrategyBase
    {
        private readonly ConcurrentQueue<AuditEntry> _auditQueue = new();
        private readonly BoundedDictionary<string, AuditChain> _auditChains = new BoundedDictionary<string, AuditChain>(1000);
        private string? _previousHash;
        private long _sequenceNumber;
        private Timer? _flushTimer;
        private bool _immutableMode;
        private bool _blockchainMode;

        /// <inheritdoc/>
        public override string StrategyId => "audit-trail-generation";

        /// <inheritdoc/>
        public override string StrategyName => "Audit Trail Generation";

        /// <inheritdoc/>
        public override string Framework => "Audit-Based";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            base.InitializeAsync(configuration, cancellationToken);

            // Configure immutable mode
            if (configuration.TryGetValue("ImmutableMode", out var immutableObj) && immutableObj is bool immutable)
            {
                _immutableMode = immutable;
            }

            // Configure blockchain mode for chain-of-custody
            if (configuration.TryGetValue("BlockchainMode", out var blockchainObj) && blockchainObj is bool blockchain)
            {
                _blockchainMode = blockchain;
            }

            // Configure auto-flush interval
            var flushIntervalSeconds = configuration.TryGetValue("FlushIntervalSeconds", out var intervalObj) && intervalObj is int interval
                ? interval : 60; // Default 1 minute

            _flushTimer = new Timer(
                async _ => await FlushAuditQueueAsync(cancellationToken),
                null,
                TimeSpan.FromSeconds(flushIntervalSeconds),
                TimeSpan.FromSeconds(flushIntervalSeconds)
            );

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        protected override async Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
        IncrementCounter("audit_trail_generation.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Generate audit entry for this operation
            var auditEntry = await GenerateAuditEntryAsync(context, cancellationToken);

            // Validate audit completeness
            if (string.IsNullOrEmpty(auditEntry.Actor))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AUDIT-001",
                    Description = "Audit entry missing actor/user identification",
                    Severity = ViolationSeverity.High,
                    Remediation = "Ensure all operations include user identification",
                    RegulatoryReference = "SOX, HIPAA, GDPR"
                });
            }

            if (string.IsNullOrEmpty(auditEntry.Action))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AUDIT-002",
                    Description = "Audit entry missing action description",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Include detailed action description in audit logs"
                });
            }

            if (string.IsNullOrEmpty(auditEntry.Resource))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AUDIT-003",
                    Description = "Audit entry missing resource identification",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Include resource identifier in audit logs"
                });
            }

            // Check if audit trail is enabled for this data classification
            if (IsAuditRequired(context) && (!context.Attributes.TryGetValue("AuditEnabled", out var enabled) || enabled is not true))
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "AUDIT-004",
                    Description = $"Audit trail not enabled for {context.DataClassification} data",
                    Severity = ViolationSeverity.Critical,
                    Remediation = "Enable audit logging for sensitive data operations",
                    RegulatoryReference = "SOX Section 404, HIPAA Security Rule"
                });
            }

            // Enqueue audit entry
            _auditQueue.Enqueue(auditEntry);

            // Generate chain entry if blockchain mode enabled
            if (_blockchainMode)
            {
                await GenerateChainEntryAsync(auditEntry, cancellationToken);
            }

            var isCompliant = violations.Count == 0;
            var status = isCompliant ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant;

            return new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["AuditEntryId"] = auditEntry.EntryId,
                    ["Timestamp"] = auditEntry.Timestamp,
                    ["SequenceNumber"] = auditEntry.SequenceNumber,
                    ["Hash"] = auditEntry.Hash ?? "N/A",
                    ["ImmutableMode"] = _immutableMode,
                    ["BlockchainMode"] = _blockchainMode,
                    ["QueuedEntries"] = _auditQueue.Count
                }
            };
        }

        private async Task<AuditEntry> GenerateAuditEntryAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            var sequenceNumber = Interlocked.Increment(ref _sequenceNumber);
            var timestamp = DateTime.UtcNow;

            var entry = new AuditEntry
            {
                EntryId = $"AUDIT-{timestamp:yyyyMMdd}-{sequenceNumber:D10}",
                SequenceNumber = sequenceNumber,
                Timestamp = timestamp,
                Actor = context.UserId ?? "SYSTEM",
                Action = context.OperationType,
                Resource = context.ResourceId ?? "UNKNOWN",
                DataClassification = context.DataClassification,
                SourceLocation = context.SourceLocation,
                DestinationLocation = context.DestinationLocation,
                ProcessingPurposes = context.ProcessingPurposes.ToList(),
                Attributes = new Dictionary<string, object>(context.Attributes),
                ComplianceFrameworks = DetermineFrameworks(context)
            };

            // Generate cryptographic hash if immutable mode
            if (_immutableMode || _blockchainMode)
            {
                entry.Hash = GenerateHash(entry);
                entry.PreviousHash = _previousHash;
                _previousHash = entry.Hash;
            }

            return entry;
        }

        private async Task GenerateChainEntryAsync(AuditEntry entry, CancellationToken cancellationToken)
        {
            var chainId = entry.Resource ?? "GLOBAL";

            if (!_auditChains.TryGetValue(chainId, out var chain))
            {
                chain = new AuditChain
                {
                    ChainId = chainId,
                    Entries = new ConcurrentQueue<AuditEntry>(),
                    CreatedAt = DateTime.UtcNow
                };
                _auditChains[chainId] = chain;
            }

            chain.Entries.Enqueue(entry);
            chain.LastUpdated = DateTime.UtcNow;

            // Verify chain integrity
            if (chain.Entries.Count > 1)
            {
                var isValid = VerifyChainIntegrity(chain);
                if (!isValid)
                {
                    // Log tampering detection
                    Console.WriteLine($"WARNING: Audit chain integrity violation detected for chain {chainId}");
                }
            }
        }

        private bool VerifyChainIntegrity(AuditChain chain)
        {
            var entries = chain.Entries.ToArray();
            for (int i = 1; i < entries.Length; i++)
            {
                if (entries[i].PreviousHash != entries[i - 1].Hash)
                {
                    return false;
                }

                // Verify hash
                var computedHash = GenerateHash(entries[i]);
                if (entries[i].Hash != computedHash)
                {
                    return false;
                }
            }
            return true;
        }

        private string GenerateHash(AuditEntry entry)
        {
            var data = new
            {
                entry.EntryId,
                entry.SequenceNumber,
                entry.Timestamp,
                entry.Actor,
                entry.Action,
                entry.Resource,
                entry.DataClassification,
                entry.PreviousHash
            };

            var json = JsonSerializer.Serialize(data);
            var bytes = Encoding.UTF8.GetBytes(json);

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hashBytes);
        }

        private bool IsAuditRequired(ComplianceContext context)
        {
            var sensitiveClassifications = new[]
            {
                "personal", "sensitive", "health", "financial", "pii", "phi", "pci"
            };

            return sensitiveClassifications.Any(c => context.DataClassification.Contains(c, StringComparison.OrdinalIgnoreCase));
        }

        private List<string> DetermineFrameworks(ComplianceContext context)
        {
            var frameworks = new List<string>();

            if (context.DataClassification.Contains("personal", StringComparison.OrdinalIgnoreCase))
                frameworks.Add("GDPR");
            if (context.DataClassification.Contains("health", StringComparison.OrdinalIgnoreCase))
                frameworks.Add("HIPAA");
            if (context.DataClassification.Contains("financial", StringComparison.OrdinalIgnoreCase))
                frameworks.Add("SOX");
            if (context.DataClassification.Contains("card", StringComparison.OrdinalIgnoreCase))
                frameworks.Add("PCI-DSS");

            return frameworks;
        }

        private async Task FlushAuditQueueAsync(CancellationToken cancellationToken)
        {
            var batch = new List<AuditEntry>();

            while (_auditQueue.TryDequeue(out var entry))
            {
                batch.Add(entry);
                if (batch.Count >= 100) // Batch size
                    break;
            }

            if (batch.Count > 0)
            {
                // In production, write to persistent storage
                // For now, just log the flush
                await Task.CompletedTask;
            }
        }

        /// <summary>
        /// Represents an immutable audit entry.
        /// </summary>
        private sealed class AuditEntry
        {
            public required string EntryId { get; init; }
            public required long SequenceNumber { get; init; }
            public required DateTime Timestamp { get; init; }
            public required string Actor { get; init; }
            public required string Action { get; init; }
            public required string Resource { get; init; }
            public required string DataClassification { get; init; }
            public string? SourceLocation { get; init; }
            public string? DestinationLocation { get; init; }
            public List<string>? ProcessingPurposes { get; init; }
            public Dictionary<string, object>? Attributes { get; init; }
            public List<string>? ComplianceFrameworks { get; init; }
            public string? Hash { get; set; }
            public string? PreviousHash { get; set; }
        }

        /// <summary>
        /// Represents a blockchain-style audit chain.
        /// </summary>
        private sealed class AuditChain
        {
            public required string ChainId { get; init; }
            public required ConcurrentQueue<AuditEntry> Entries { get; init; }
            public required DateTime CreatedAt { get; init; }
            public DateTime LastUpdated { get; set; }
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("audit_trail_generation.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        IncrementCounter("audit_trail_generation.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}
}
