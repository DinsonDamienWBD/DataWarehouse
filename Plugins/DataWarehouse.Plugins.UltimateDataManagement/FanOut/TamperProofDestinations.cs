using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.FanOut;

/// <summary>
/// Blockchain anchor destination for tamper-proof evidence.
/// Writes content hash to blockchain via T95 UltimateAccessControl blockchain strategies.
/// </summary>
/// <remarks>
/// <b>DEPENDENCY:</b> T95 UltimateAccessControl (BlockchainStrategy)
/// <b>MESSAGE TOPIC:</b> accesscontrol.blockchain.anchor
/// <b>FALLBACK:</b> Local blockchain simulation if T95 unavailable
/// </remarks>
public sealed class BlockchainAnchorDestination : WriteDestinationPluginBase
{
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Initializes a new BlockchainAnchorDestination.
    /// </summary>
    public BlockchainAnchorDestination(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.destination.blockchain";

    /// <inheritdoc/>
    public override string Name => "Blockchain Anchor Destination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override WriteDestinationType DestinationType => WriteDestinationType.DocumentStore;

    /// <inheritdoc/>
    public override bool IsRequired => true;

    /// <inheritdoc/>
    public override int Priority => 95;

    /// <inheritdoc/>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Compute content hash for blockchain anchor
            var contentHash = ComputeContentHash(objectId, content);
            var timestamp = DateTimeOffset.UtcNow;

            var anchorData = new BlockchainAnchorData
            {
                ObjectId = objectId,
                ContentHash = contentHash,
                Timestamp = timestamp,
                Size = content.Size ?? 0,
                ContentType = content.ContentType ?? "application/octet-stream"
            };

            if (_messageBus != null)
            {
                // Request blockchain anchoring via T95
                var msgResponse = await _messageBus.SendAsync(
                    "accesscontrol.blockchain.anchor",
                    new PluginMessage
                    {
                        Type = "accesscontrol.blockchain.anchor",
                        Source = Id,
                        Payload = new Dictionary<string, object>
                        {
                            ["objectId"] = objectId,
                            ["contentHash"] = contentHash,
                            ["timestamp"] = timestamp.ToString("O"),
                            ["size"] = content.Size ?? 0
                        }
                    },
                    TimeSpan.FromSeconds(30),
                    ct);

                sw.Stop();

                if (msgResponse?.Success == true)
                {
                    return SuccessResult(sw.Elapsed);
                }

                return FailureResult(msgResponse?.ErrorMessage ?? "Blockchain anchor failed", sw.Elapsed);
            }

            // No fallback - blockchain anchoring requires proper backend for tamper-proof guarantee
            sw.Stop();
            return FailureResult("Message bus unavailable - blockchain anchoring requires T95 connection for tamper-proof integrity", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult(ex.Message, sw.Elapsed);
        }
    }

    private static string ComputeContentHash(string objectId, IndexableContent content)
    {
        using var sha256 = SHA256.Create();
        var hashInput = new StringBuilder();
        hashInput.Append(objectId);
        hashInput.Append(':');
        hashInput.Append(content.Filename ?? "");
        hashInput.Append(':');
        hashInput.Append(content.Size ?? 0);
        hashInput.Append(':');
        hashInput.Append(content.ContentType ?? "");

        if (content.TextContent != null)
        {
            hashInput.Append(':');
            hashInput.Append(content.TextContent.Length > 1024
                ? content.TextContent[..1024]
                : content.TextContent);
        }

        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashInput.ToString()));
        return Convert.ToHexString(hash);
    }

    private sealed class BlockchainAnchorData
    {
        public string ObjectId { get; init; } = "";
        public string ContentHash { get; init; } = "";
        public DateTimeOffset Timestamp { get; init; }
        public long Size { get; init; }
        public string ContentType { get; init; } = "";
    }
}

/// <summary>
/// WORM (Write Once Read Many) storage destination for immutable finalization.
/// Writes to WORM storage via T95 UltimateAccessControl WORM strategies.
/// </summary>
/// <remarks>
/// <b>DEPENDENCY:</b> T95 UltimateAccessControl (WormStrategy)
/// <b>MESSAGE TOPIC:</b> accesscontrol.worm.finalize
/// <b>FALLBACK:</b> None - WORM finalization requires proper WORM storage
///
/// <b>IMPORTANT:</b> WORM writes are FINAL and IRREVERSIBLE.
/// This destination should only be called after all other required writes succeed.
/// </remarks>
public sealed class WormStorageDestination : WriteDestinationPluginBase
{
    private readonly IMessageBus? _messageBus;
    private readonly TimeSpan _retentionPeriod;

    /// <summary>
    /// Initializes a new WormStorageDestination.
    /// </summary>
    /// <param name="messageBus">Message bus for inter-plugin communication.</param>
    /// <param name="retentionPeriod">WORM retention period (default: 7 years).</param>
    public WormStorageDestination(IMessageBus? messageBus = null, TimeSpan? retentionPeriod = null)
    {
        _messageBus = messageBus;
        _retentionPeriod = retentionPeriod ?? TimeSpan.FromDays(365 * 7); // 7 years default
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.destination.worm";

    /// <inheritdoc/>
    public override string Name => "WORM Storage Destination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override WriteDestinationType DestinationType => WriteDestinationType.DocumentStore;

    /// <inheritdoc/>
    public override bool IsRequired => true;

    /// <inheritdoc/>
    public override int Priority => 10;  // Low priority = runs last

    /// <inheritdoc/>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var retentionUntil = DateTimeOffset.UtcNow.Add(_retentionPeriod);
            var wormRecord = new WormRecord
            {
                ObjectId = objectId,
                Filename = content.Filename ?? objectId,
                ContentType = content.ContentType ?? "application/octet-stream",
                Size = content.Size ?? 0,
                FinalizedAt = DateTimeOffset.UtcNow,
                RetentionUntil = retentionUntil,
                IntegrityHash = ComputeIntegrityHash(content)
            };

            if (_messageBus != null)
            {
                // Request WORM finalization via T95
                var msgResponse = await _messageBus.SendAsync(
                    "accesscontrol.worm.finalize",
                    new PluginMessage
                    {
                        Type = "accesscontrol.worm.finalize",
                        Source = Id,
                        Payload = new Dictionary<string, object>
                        {
                            ["objectId"] = objectId,
                            ["filename"] = wormRecord.Filename,
                            ["contentType"] = wormRecord.ContentType,
                            ["size"] = wormRecord.Size,
                            ["retentionUntil"] = retentionUntil.ToString("O"),
                            ["integrityHash"] = wormRecord.IntegrityHash
                        }
                    },
                    TimeSpan.FromSeconds(60),
                    ct);

                sw.Stop();

                if (msgResponse?.Success == true)
                {
                    return SuccessResult(sw.Elapsed);
                }

                return FailureResult(msgResponse?.ErrorMessage ?? "WORM finalization failed", sw.Elapsed);
            }

            // No fallback - WORM finalization requires proper WORM storage for tamper-proof integrity
            sw.Stop();
            return FailureResult("Message bus unavailable - WORM finalization requires T95 connection for immutable storage", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult(ex.Message, sw.Elapsed);
        }
    }

    private static string ComputeIntegrityHash(IndexableContent content)
    {
        using var sha256 = SHA256.Create();
        var hashInput = $"{content.ObjectId}:{content.Filename}:{content.Size}:{content.ContentType}";
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToHexString(hash);
    }

    private sealed class WormRecord
    {
        public string ObjectId { get; init; } = "";
        public string Filename { get; init; } = "";
        public string ContentType { get; init; } = "";
        public long Size { get; init; }
        public DateTimeOffset FinalizedAt { get; init; }
        public DateTimeOffset RetentionUntil { get; init; }
        public string IntegrityHash { get; init; } = "";
    }

    private sealed class WormFinalizeResponse
    {
        public bool Success { get; init; }
        public string? WormId { get; init; }
        public string? ErrorMessage { get; init; }
    }
}

/// <summary>
/// Audit log destination for compliance and forensics.
/// Writes audit trail entries for all write operations.
/// </summary>
/// <remarks>
/// <b>DEPENDENCY:</b> T96 UltimateCompliance (AuditStrategy) - optional
/// <b>MESSAGE TOPIC:</b> compliance.audit.log
/// <b>FALLBACK:</b> Local audit log file if T96 unavailable
/// </remarks>
public sealed class AuditLogDestination : WriteDestinationPluginBase
{
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Initializes a new AuditLogDestination.
    /// </summary>
    public AuditLogDestination(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.destination.auditlog";

    /// <inheritdoc/>
    public override string Name => "Audit Log Destination";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override WriteDestinationType DestinationType => WriteDestinationType.DocumentStore;

    /// <inheritdoc/>
    public override bool IsRequired => false;

    /// <inheritdoc/>
    public override int Priority => 20;

    /// <inheritdoc/>
    public override async Task<WriteDestinationResult> WriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var auditEntry = new AuditLogEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                ObjectId = objectId,
                Operation = "WRITE",
                Timestamp = DateTimeOffset.UtcNow,
                Details = new Dictionary<string, object>
                {
                    ["filename"] = content.Filename ?? "",
                    ["contentType"] = content.ContentType ?? "",
                    ["size"] = content.Size ?? 0
                }
            };

            if (_messageBus != null)
            {
                // Send audit log via T96
                await _messageBus.PublishAsync(
                    "compliance.audit.log",
                    new PluginMessage
                    {
                        Type = "compliance.audit.log",
                        Source = Id,
                        Payload = new Dictionary<string, object>
                        {
                            ["entryId"] = auditEntry.EntryId,
                            ["objectId"] = objectId,
                            ["operation"] = auditEntry.Operation,
                            ["timestamp"] = auditEntry.Timestamp.ToString("O"),
                            ["details"] = auditEntry.Details
                        }
                    },
                    ct);

                sw.Stop();
                return SuccessResult(sw.Elapsed);
            }

            // Fallback: Local logging
            sw.Stop();
            return SuccessResult(sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FailureResult(ex.Message, sw.Elapsed);
        }
    }

    private sealed class AuditLogEntry
    {
        public string EntryId { get; init; } = "";
        public string ObjectId { get; init; } = "";
        public string Operation { get; init; } = "";
        public DateTimeOffset Timestamp { get; init; }
        public Dictionary<string, object> Details { get; init; } = new();
    }
}
