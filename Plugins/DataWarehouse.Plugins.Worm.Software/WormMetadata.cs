// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using System.Text.Json;

namespace DataWarehouse.Plugins.Worm.Software;

/// <summary>
/// Metadata structure stored in JSON sidecar files for software WORM enforcement.
/// Contains all information needed to enforce retention policies and legal holds.
/// </summary>
public class WormMetadata
{
    /// <summary>
    /// Object identifier.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// UTC timestamp when the object was written to WORM storage.
    /// </summary>
    public required DateTimeOffset WrittenAt { get; init; }

    /// <summary>
    /// UTC timestamp when retention period expires.
    /// After this time (with no legal holds), object may be deleted.
    /// </summary>
    public required DateTimeOffset RetentionExpiry { get; init; }

    /// <summary>
    /// Content hash of the WORM object for integrity verification.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Hash algorithm used for content verification.
    /// </summary>
    public required HashAlgorithmType HashAlgorithm { get; init; }

    /// <summary>
    /// Size of the WORM object in bytes.
    /// </summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Author who wrote the object.
    /// </summary>
    public required string Author { get; init; }

    /// <summary>
    /// Comment explaining the write operation.
    /// </summary>
    public required string Comment { get; init; }

    /// <summary>
    /// Active legal holds on this object.
    /// If any holds exist, object cannot be deleted even after retention expires.
    /// </summary>
    public List<LegalHoldRecord> LegalHolds { get; init; } = new();

    /// <summary>
    /// File system path where the actual data is stored.
    /// </summary>
    public required string DataFilePath { get; init; }

    /// <summary>
    /// Provider-specific metadata.
    /// </summary>
    public Dictionary<string, string>? ProviderMetadata { get; init; }

    /// <summary>
    /// Serializes this metadata to JSON.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    /// <summary>
    /// Deserializes metadata from JSON.
    /// </summary>
    public static WormMetadata FromJson(string json)
    {
        var metadata = JsonSerializer.Deserialize<WormMetadata>(json);
        if (metadata == null)
        {
            throw new InvalidOperationException("Failed to deserialize WORM metadata from JSON.");
        }
        return metadata;
    }

    /// <summary>
    /// Creates metadata from a write operation.
    /// </summary>
    public static WormMetadata Create(
        Guid objectId,
        DateTimeOffset writtenAt,
        DateTimeOffset retentionExpiry,
        string contentHash,
        HashAlgorithmType hashAlgorithm,
        long sizeBytes,
        string author,
        string comment,
        string dataFilePath,
        Dictionary<string, string>? providerMetadata = null)
    {
        return new WormMetadata
        {
            ObjectId = objectId,
            WrittenAt = writtenAt,
            RetentionExpiry = retentionExpiry,
            ContentHash = contentHash,
            HashAlgorithm = hashAlgorithm,
            SizeBytes = sizeBytes,
            Author = author,
            Comment = comment,
            DataFilePath = dataFilePath,
            ProviderMetadata = providerMetadata,
            LegalHolds = new List<LegalHoldRecord>()
        };
    }
}

/// <summary>
/// Record of a legal hold stored in metadata.
/// </summary>
public class LegalHoldRecord
{
    /// <summary>
    /// Unique identifier for this legal hold.
    /// </summary>
    public required string HoldId { get; init; }

    /// <summary>
    /// Reason for the legal hold.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// UTC timestamp when the hold was placed.
    /// </summary>
    public required DateTimeOffset PlacedAt { get; init; }

    /// <summary>
    /// Principal who placed the hold.
    /// </summary>
    public required string PlacedBy { get; init; }

    /// <summary>
    /// Optional case number or reference.
    /// </summary>
    public string? CaseNumber { get; init; }

    /// <summary>
    /// Optional expiry date for the hold.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Additional metadata about the hold.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Converts to SDK LegalHold type.
    /// </summary>
    public LegalHold ToLegalHold()
    {
        return new LegalHold
        {
            HoldId = HoldId,
            Reason = Reason,
            PlacedAt = PlacedAt,
            PlacedBy = PlacedBy,
            CaseNumber = CaseNumber,
            ExpiresAt = ExpiresAt,
            Metadata = Metadata
        };
    }

    /// <summary>
    /// Creates from SDK LegalHold type.
    /// </summary>
    public static LegalHoldRecord FromLegalHold(LegalHold hold)
    {
        return new LegalHoldRecord
        {
            HoldId = hold.HoldId,
            Reason = hold.Reason,
            PlacedAt = hold.PlacedAt,
            PlacedBy = hold.PlacedBy,
            CaseNumber = hold.CaseNumber,
            ExpiresAt = hold.ExpiresAt,
            Metadata = hold.Metadata
        };
    }
}
