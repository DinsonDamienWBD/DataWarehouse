// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Main manifest for each tamper-proof object.
/// Contains all metadata needed to reconstruct, verify, and audit the object.
/// This manifest is stored separately from shards and includes complete pipeline history.
/// </summary>
public class TamperProofManifest
{
    /// <summary>
    /// Unique identifier for this object.
    /// Generated on first write and preserved through corrections.
    /// </summary>
    public required Guid ObjectId { get; init; }

    /// <summary>
    /// Version number for this object (starts at 1).
    /// Incremented with each correction.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// UTC timestamp when this version was written.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Write context captured at the time of creation.
    /// Includes author, comment, and attribution metadata.
    /// </summary>
    public required WriteContextRecord WriteContext { get; init; }

    /// <summary>
    /// Hash algorithm used for all integrity verification.
    /// Determines the size and security of hash values throughout the manifest.
    /// </summary>
    public required HashAlgorithmType HashAlgorithm { get; init; }

    /// <summary>
    /// Hash of the original content (before any pipeline transformations).
    /// This is the definitive integrity check - if pipeline is reversed correctly,
    /// the result must match this hash.
    /// </summary>
    public required string OriginalContentHash { get; init; }

    /// <summary>
    /// Size of the original content in bytes (before pipeline).
    /// </summary>
    public required long OriginalContentSize { get; init; }

    /// <summary>
    /// Hash of the final content (after all pipeline transformations).
    /// This is what gets sharded and stored. Used for quick verification
    /// without running the full pipeline in reverse.
    /// </summary>
    public required string FinalContentHash { get; init; }

    /// <summary>
    /// Size of the final content in bytes (after pipeline, before sharding).
    /// </summary>
    public required long FinalContentSize { get; init; }

    /// <summary>
    /// Ordered list of pipeline stages applied to the content.
    /// Must be reversed in opposite order during reads.
    /// Empty if no pipeline transformations were applied.
    /// </summary>
    public required IReadOnlyList<PipelineStageRecord> PipelineStages { get; init; }

    /// <summary>
    /// RAID distribution configuration and shard details.
    /// Describes how the final content was split into shards.
    /// </summary>
    public required RaidRecord RaidConfiguration { get; init; }

    /// <summary>
    /// Content padding applied before pipeline (Phase 1 padding).
    /// Optional - only present if content padding was configured.
    /// This is the first layer of obfuscation.
    /// </summary>
    public ContentPaddingRecord? ContentPadding { get; init; }

    /// <summary>
    /// Reference to WORM storage backup.
    /// Optional - only present if WORM storage is configured.
    /// </summary>
    public WormReference? WormBackup { get; init; }

    /// <summary>
    /// Reference to blockchain anchor.
    /// Optional - only present if blockchain anchoring is enabled.
    /// </summary>
    public BlockchainAnchorReference? BlockchainAnchor { get; init; }

    /// <summary>
    /// Reference to the previous version of this object.
    /// Null for version 1.
    /// </summary>
    public Guid? PreviousVersionId { get; init; }

    /// <summary>
    /// If this is a correction, contains the correction context.
    /// Null for original writes.
    /// </summary>
    public CorrectionContextRecord? CorrectionContext { get; init; }

    /// <summary>
    /// Optional user-defined metadata.
    /// Stored for searchability and categorization.
    /// </summary>
    public Dictionary<string, object>? UserMetadata { get; init; }

    /// <summary>
    /// Optional content type/MIME type of the original content.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Optional original filename if this object was uploaded from a file.
    /// </summary>
    public string? OriginalFilename { get; init; }

    /// <summary>
    /// WORM retention period (if WORM is enabled).
    /// After this time, the WORM backup can be deleted.
    /// </summary>
    public TimeSpan? WormRetentionPeriod { get; init; }

    /// <summary>
    /// UTC timestamp when WORM retention expires.
    /// Calculated as CreatedAt + WormRetentionPeriod.
    /// </summary>
    public DateTimeOffset? WormRetentionExpiresAt { get; init; }

    /// <summary>
    /// Validates the manifest for completeness and internal consistency.
    /// </summary>
    /// <returns>List of validation errors, empty if valid.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (ObjectId == Guid.Empty)
        {
            errors.Add("ObjectId cannot be empty.");
        }

        if (Version < 1)
        {
            errors.Add("Version must be >= 1.");
        }

        if (string.IsNullOrWhiteSpace(OriginalContentHash))
        {
            errors.Add("OriginalContentHash is required.");
        }

        if (string.IsNullOrWhiteSpace(FinalContentHash))
        {
            errors.Add("FinalContentHash is required.");
        }

        if (OriginalContentSize < 0)
        {
            errors.Add("OriginalContentSize cannot be negative.");
        }

        if (FinalContentSize < 0)
        {
            errors.Add("FinalContentSize cannot be negative.");
        }

        if (RaidConfiguration == null)
        {
            errors.Add("RaidConfiguration is required.");
        }
        else
        {
            var raidErrors = RaidConfiguration.Validate();
            errors.AddRange(raidErrors);
        }

        if (PipelineStages == null)
        {
            errors.Add("PipelineStages is required (can be empty).");
        }
        else
        {
            for (int i = 0; i < PipelineStages.Count; i++)
            {
                var stageErrors = PipelineStages[i].Validate();
                errors.AddRange(stageErrors.Select(e => $"PipelineStages[{i}]: {e}"));
            }
        }

        if (Version > 1 && PreviousVersionId == null)
        {
            errors.Add("PreviousVersionId is required for versions > 1.");
        }

        if (Version > 1 && CorrectionContext == null)
        {
            errors.Add("CorrectionContext is required for corrections (versions > 1).");
        }

        if (WormRetentionPeriod.HasValue && !WormRetentionExpiresAt.HasValue)
        {
            errors.Add("WormRetentionExpiresAt must be set when WormRetentionPeriod is present.");
        }

        if (WormBackup != null)
        {
            var wormErrors = WormBackup.Validate();
            errors.AddRange(wormErrors.Select(e => $"WormBackup: {e}"));
        }

        if (BlockchainAnchor != null)
        {
            var bcErrors = BlockchainAnchor.Validate();
            errors.AddRange(bcErrors.Select(e => $"BlockchainAnchor: {e}"));
        }

        if (ContentPadding != null)
        {
            var paddingErrors = ContentPadding.Validate();
            errors.AddRange(paddingErrors.Select(e => $"ContentPadding: {e}"));
        }

        return errors;
    }

    /// <summary>
    /// Computes a cryptographic hash of the entire manifest for integrity verification.
    /// This hash can be used to detect tampering of the manifest itself.
    /// </summary>
    public string ComputeManifestHash()
    {
        // Create a deterministic string representation
        var components = new List<string>
        {
            ObjectId.ToString(),
            Version.ToString(),
            CreatedAt.ToString("O"),
            OriginalContentHash,
            OriginalContentSize.ToString(),
            FinalContentHash,
            FinalContentSize.ToString(),
            HashAlgorithm.ToString(),
            WriteContext.Author,
            WriteContext.Comment,
            WriteContext.Timestamp.ToString("O")
        };

        // Add pipeline stages
        foreach (var stage in PipelineStages)
        {
            components.Add($"{stage.StageType}:{stage.InputHash}:{stage.OutputHash}");
        }

        // Add RAID shards
        foreach (var shard in RaidConfiguration.Shards)
        {
            components.Add($"{shard.ShardIndex}:{shard.ContentHash}");
        }

        var data = string.Join("|", components);
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Creates a deep copy of this manifest.
    /// </summary>
    public TamperProofManifest Clone()
    {
        return new TamperProofManifest
        {
            ObjectId = ObjectId,
            Version = Version,
            CreatedAt = CreatedAt,
            WriteContext = WriteContext,
            HashAlgorithm = HashAlgorithm,
            OriginalContentHash = OriginalContentHash,
            OriginalContentSize = OriginalContentSize,
            FinalContentHash = FinalContentHash,
            FinalContentSize = FinalContentSize,
            PipelineStages = PipelineStages.Select(s => s.Clone()).ToList(),
            RaidConfiguration = RaidConfiguration.Clone(),
            ContentPadding = ContentPadding?.Clone(),
            WormBackup = WormBackup?.Clone(),
            BlockchainAnchor = BlockchainAnchor?.Clone(),
            PreviousVersionId = PreviousVersionId,
            CorrectionContext = CorrectionContext,
            UserMetadata = UserMetadata != null ? new Dictionary<string, object>(UserMetadata) : null,
            ContentType = ContentType,
            OriginalFilename = OriginalFilename,
            WormRetentionPeriod = WormRetentionPeriod,
            WormRetentionExpiresAt = WormRetentionExpiresAt
        };
    }
}

/// <summary>
/// Records a single pipeline transformation stage.
/// Pipeline stages must be reversed in opposite order during reads.
/// </summary>
public class PipelineStageRecord
{
    /// <summary>
    /// Type of transformation (e.g., "compression", "encryption").
    /// Must match a registered pipeline stage handler.
    /// </summary>
    public required string StageType { get; init; }

    /// <summary>
    /// Order of this stage in the pipeline (0-based).
    /// Stage 0 operates on original content, Stage N produces final content.
    /// </summary>
    public required int StageIndex { get; init; }

    /// <summary>
    /// Hash of the content before this stage was applied.
    /// Used to verify pipeline integrity at each step.
    /// </summary>
    public required string InputHash { get; init; }

    /// <summary>
    /// Hash of the content after this stage was applied.
    /// </summary>
    public required string OutputHash { get; init; }

    /// <summary>
    /// Size in bytes before this stage.
    /// </summary>
    public required long InputSize { get; init; }

    /// <summary>
    /// Size in bytes after this stage.
    /// </summary>
    public required long OutputSize { get; init; }

    /// <summary>
    /// Stage-specific configuration parameters.
    /// For example, compression might include algorithm and level.
    /// Stored as JSON-serializable dictionary.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }

    /// <summary>
    /// UTC timestamp when this stage was executed.
    /// </summary>
    public required DateTimeOffset ExecutedAt { get; init; }

    /// <summary>
    /// Duration of this stage execution.
    /// Useful for performance profiling and anomaly detection.
    /// </summary>
    public TimeSpan? ExecutionDuration { get; init; }

    /// <summary>
    /// Validates the stage record.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(StageType))
        {
            errors.Add("StageType is required.");
        }

        if (StageIndex < 0)
        {
            errors.Add("StageIndex cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(InputHash))
        {
            errors.Add("InputHash is required.");
        }

        if (string.IsNullOrWhiteSpace(OutputHash))
        {
            errors.Add("OutputHash is required.");
        }

        if (InputSize < 0)
        {
            errors.Add("InputSize cannot be negative.");
        }

        if (OutputSize < 0)
        {
            errors.Add("OutputSize cannot be negative.");
        }

        return errors;
    }

    /// <summary>
    /// Creates a deep copy of this stage record.
    /// </summary>
    public PipelineStageRecord Clone()
    {
        return new PipelineStageRecord
        {
            StageType = StageType,
            StageIndex = StageIndex,
            InputHash = InputHash,
            OutputHash = OutputHash,
            InputSize = InputSize,
            OutputSize = OutputSize,
            Parameters = Parameters != null ? new Dictionary<string, object>(Parameters) : null,
            ExecutedAt = ExecutedAt,
            ExecutionDuration = ExecutionDuration
        };
    }
}

/// <summary>
/// Records RAID distribution configuration and all shard details.
/// </summary>
public class RaidRecord
{
    /// <summary>
    /// Total number of data shards.
    /// The original content is split into this many pieces.
    /// </summary>
    public required int DataShardCount { get; init; }

    /// <summary>
    /// Number of parity shards for redundancy.
    /// Can lose up to this many shards and still reconstruct.
    /// </summary>
    public required int ParityShardCount { get; init; }

    /// <summary>
    /// Total number of shards (data + parity).
    /// </summary>
    [JsonIgnore]
    public int TotalShardCount => DataShardCount + ParityShardCount;

    /// <summary>
    /// Size of each data shard in bytes.
    /// All data shards have the same size (last one may be padded).
    /// </summary>
    public required int ShardSize { get; init; }

    /// <summary>
    /// Details for each shard (data + parity).
    /// Ordered by shard index.
    /// </summary>
    public required IReadOnlyList<ShardRecord> Shards { get; init; }

    /// <summary>
    /// Validates the RAID configuration.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (DataShardCount < 1)
        {
            errors.Add("DataShardCount must be >= 1.");
        }

        if (ParityShardCount < 0)
        {
            errors.Add("ParityShardCount cannot be negative.");
        }

        if (ShardSize < 1)
        {
            errors.Add("ShardSize must be >= 1.");
        }

        if (Shards == null)
        {
            errors.Add("Shards collection is required.");
        }
        else
        {
            if (Shards.Count != TotalShardCount)
            {
                errors.Add($"Shard count mismatch: expected {TotalShardCount}, got {Shards.Count}.");
            }

            for (int i = 0; i < Shards.Count; i++)
            {
                if (Shards[i].ShardIndex != i)
                {
                    errors.Add($"Shard at position {i} has incorrect ShardIndex {Shards[i].ShardIndex}.");
                }

                var shardErrors = Shards[i].Validate();
                errors.AddRange(shardErrors.Select(e => $"Shard[{i}]: {e}"));
            }
        }

        return errors;
    }

    /// <summary>
    /// Creates a deep copy of this RAID record.
    /// </summary>
    public RaidRecord Clone()
    {
        return new RaidRecord
        {
            DataShardCount = DataShardCount,
            ParityShardCount = ParityShardCount,
            ShardSize = ShardSize,
            Shards = Shards.Select(s => s.Clone()).ToList()
        };
    }
}

/// <summary>
/// Records details for a single shard (data or parity).
/// </summary>
public class ShardRecord
{
    /// <summary>
    /// Zero-based index of this shard.
    /// Indices 0 to (DataShardCount-1) are data shards.
    /// Remaining indices are parity shards.
    /// </summary>
    public required int ShardIndex { get; init; }

    /// <summary>
    /// True if this is a parity shard, false if data shard.
    /// </summary>
    public required bool IsParity { get; init; }

    /// <summary>
    /// Actual size of this shard in bytes.
    /// May differ from RaidRecord.ShardSize due to padding.
    /// </summary>
    public required int ActualSize { get; init; }

    /// <summary>
    /// Hash of the shard content for integrity verification.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Storage location identifier for this shard.
    /// Format depends on storage backend (e.g., file path, blob URI, S3 key).
    /// </summary>
    public required string StorageLocation { get; init; }

    /// <summary>
    /// Optional storage tier or instance identifier.
    /// Used for multi-tier or distributed storage.
    /// </summary>
    public string? StorageTier { get; init; }

    /// <summary>
    /// Shard padding details (Phase 2 obfuscation).
    /// Optional - only present if shard padding is enabled.
    /// </summary>
    public ShardPaddingRecord? Padding { get; init; }

    /// <summary>
    /// UTC timestamp when this shard was written.
    /// </summary>
    public required DateTimeOffset WrittenAt { get; init; }

    /// <summary>
    /// Validates the shard record.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (ShardIndex < 0)
        {
            errors.Add("ShardIndex cannot be negative.");
        }

        if (ActualSize < 0)
        {
            errors.Add("ActualSize cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(ContentHash))
        {
            errors.Add("ContentHash is required.");
        }

        if (string.IsNullOrWhiteSpace(StorageLocation))
        {
            errors.Add("StorageLocation is required.");
        }

        if (Padding != null)
        {
            var paddingErrors = Padding.Validate();
            errors.AddRange(paddingErrors.Select(e => $"Padding: {e}"));
        }

        return errors;
    }

    /// <summary>
    /// Creates a deep copy of this shard record.
    /// </summary>
    public ShardRecord Clone()
    {
        return new ShardRecord
        {
            ShardIndex = ShardIndex,
            IsParity = IsParity,
            ActualSize = ActualSize,
            ContentHash = ContentHash,
            StorageLocation = StorageLocation,
            StorageTier = StorageTier,
            Padding = Padding?.Clone(),
            WrittenAt = WrittenAt
        };
    }
}

/// <summary>
/// Records shard padding details (Phase 2 obfuscation).
/// Optional, user-configurable per-shard random padding.
/// </summary>
public class ShardPaddingRecord
{
    /// <summary>
    /// Number of padding bytes added to the beginning of the shard.
    /// </summary>
    public required int PrefixPaddingBytes { get; init; }

    /// <summary>
    /// Number of padding bytes added to the end of the shard.
    /// </summary>
    public required int SuffixPaddingBytes { get; init; }

    /// <summary>
    /// Total padding bytes (prefix + suffix).
    /// </summary>
    [JsonIgnore]
    public int TotalPaddingBytes => PrefixPaddingBytes + SuffixPaddingBytes;

    /// <summary>
    /// Hash of the padding bytes for verification.
    /// Optional - can be omitted if not needed.
    /// </summary>
    public string? PaddingHash { get; init; }

    /// <summary>
    /// Seed used for generating padding (if deterministic).
    /// Optional - null if random padding was used.
    /// </summary>
    public long? PaddingSeed { get; init; }

    /// <summary>
    /// Validates the padding record.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (PrefixPaddingBytes < 0)
        {
            errors.Add("PrefixPaddingBytes cannot be negative.");
        }

        if (SuffixPaddingBytes < 0)
        {
            errors.Add("SuffixPaddingBytes cannot be negative.");
        }

        if (TotalPaddingBytes == 0)
        {
            errors.Add("At least one of PrefixPaddingBytes or SuffixPaddingBytes must be > 0.");
        }

        return errors;
    }

    /// <summary>
    /// Creates a deep copy of this padding record.
    /// </summary>
    public ShardPaddingRecord Clone()
    {
        return new ShardPaddingRecord
        {
            PrefixPaddingBytes = PrefixPaddingBytes,
            SuffixPaddingBytes = SuffixPaddingBytes,
            PaddingHash = PaddingHash,
            PaddingSeed = PaddingSeed
        };
    }
}

/// <summary>
/// Records content padding details (Phase 1 obfuscation).
/// Applied to original content before pipeline transformations.
/// </summary>
public class ContentPaddingRecord
{
    /// <summary>
    /// Number of padding bytes added to the beginning of the content.
    /// </summary>
    public required int PrefixPaddingBytes { get; init; }

    /// <summary>
    /// Number of padding bytes added to the end of the content.
    /// </summary>
    public required int SuffixPaddingBytes { get; init; }

    /// <summary>
    /// Total padding bytes (prefix + suffix).
    /// </summary>
    [JsonIgnore]
    public int TotalPaddingBytes => PrefixPaddingBytes + SuffixPaddingBytes;

    /// <summary>
    /// Hash of the padding bytes for verification.
    /// Optional - can be omitted if not needed.
    /// </summary>
    public string? PaddingHash { get; init; }

    /// <summary>
    /// Seed used for generating padding (if deterministic).
    /// Optional - null if random padding was used.
    /// </summary>
    public long? PaddingSeed { get; init; }

    /// <summary>
    /// Pattern used for padding (e.g., "random", "zeros", "custom").
    /// </summary>
    public string? PaddingPattern { get; init; }

    /// <summary>
    /// Validates the content padding record.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (PrefixPaddingBytes < 0)
        {
            errors.Add("PrefixPaddingBytes cannot be negative.");
        }

        if (SuffixPaddingBytes < 0)
        {
            errors.Add("SuffixPaddingBytes cannot be negative.");
        }

        if (TotalPaddingBytes == 0)
        {
            errors.Add("At least one of PrefixPaddingBytes or SuffixPaddingBytes must be > 0.");
        }

        return errors;
    }

    /// <summary>
    /// Creates a deep copy of this content padding record.
    /// </summary>
    public ContentPaddingRecord Clone()
    {
        return new ContentPaddingRecord
        {
            PrefixPaddingBytes = PrefixPaddingBytes,
            SuffixPaddingBytes = SuffixPaddingBytes,
            PaddingHash = PaddingHash,
            PaddingSeed = PaddingSeed,
            PaddingPattern = PaddingPattern
        };
    }
}

/// <summary>
/// Reference to WORM (Write-Once-Read-Many) storage backup.
/// Contains information needed to retrieve the immutable backup.
/// </summary>
public class WormReference
{
    /// <summary>
    /// Storage location identifier for the WORM backup.
    /// Format depends on WORM backend (e.g., S3 Object Lock ARN, Azure Immutable Blob URI).
    /// </summary>
    public required string StorageLocation { get; init; }

    /// <summary>
    /// Hash of the WORM backup content for verification.
    /// Should match FinalContentHash from manifest.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>
    /// Size of the WORM backup in bytes.
    /// </summary>
    public required long ContentSize { get; init; }

    /// <summary>
    /// UTC timestamp when the WORM backup was written.
    /// </summary>
    public required DateTimeOffset WrittenAt { get; init; }

    /// <summary>
    /// UTC timestamp when WORM retention expires and backup can be deleted.
    /// </summary>
    public DateTimeOffset? RetentionExpiresAt { get; init; }

    /// <summary>
    /// WORM enforcement mode used for this backup.
    /// </summary>
    public required WormEnforcementMode EnforcementMode { get; init; }

    /// <summary>
    /// Provider-specific metadata for WORM storage.
    /// May include lock ID, legal hold status, etc.
    /// </summary>
    public Dictionary<string, object>? ProviderMetadata { get; init; }

    /// <summary>
    /// Validates the WORM reference.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(StorageLocation))
        {
            errors.Add("StorageLocation is required.");
        }

        if (string.IsNullOrWhiteSpace(ContentHash))
        {
            errors.Add("ContentHash is required.");
        }

        if (ContentSize < 0)
        {
            errors.Add("ContentSize cannot be negative.");
        }

        return errors;
    }

    /// <summary>
    /// Creates a deep copy of this WORM reference.
    /// </summary>
    public WormReference Clone()
    {
        return new WormReference
        {
            StorageLocation = StorageLocation,
            ContentHash = ContentHash,
            ContentSize = ContentSize,
            WrittenAt = WrittenAt,
            RetentionExpiresAt = RetentionExpiresAt,
            EnforcementMode = EnforcementMode,
            ProviderMetadata = ProviderMetadata != null ? new Dictionary<string, object>(ProviderMetadata) : null
        };
    }
}

/// <summary>
/// Reference to blockchain anchor for immutability verification.
/// Contains information needed to verify the blockchain proof.
/// </summary>
public class BlockchainAnchorReference
{
    /// <summary>
    /// Blockchain network identifier (e.g., "ethereum-mainnet", "polygon-mumbai").
    /// </summary>
    public required string Network { get; init; }

    /// <summary>
    /// Transaction hash or ID on the blockchain.
    /// </summary>
    public required string TransactionId { get; init; }

    /// <summary>
    /// Block number or height where the transaction was confirmed.
    /// </summary>
    public required long BlockNumber { get; init; }

    /// <summary>
    /// Block hash for verification.
    /// </summary>
    public string? BlockHash { get; init; }

    /// <summary>
    /// UTC timestamp of the blockchain block.
    /// </summary>
    public required DateTimeOffset BlockTimestamp { get; init; }

    /// <summary>
    /// Hash that was anchored on the blockchain.
    /// Typically the manifest hash or content hash.
    /// </summary>
    public required string AnchoredHash { get; init; }

    /// <summary>
    /// Smart contract address (if applicable).
    /// </summary>
    public string? ContractAddress { get; init; }

    /// <summary>
    /// Number of confirmations at the time of anchoring.
    /// Higher confirmations mean stronger immutability guarantees.
    /// </summary>
    public int? Confirmations { get; init; }

    /// <summary>
    /// Provider-specific metadata for blockchain anchoring.
    /// May include gas price, explorer URLs, etc.
    /// </summary>
    public Dictionary<string, object>? ProviderMetadata { get; init; }

    /// <summary>
    /// Validates the blockchain anchor reference.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Network))
        {
            errors.Add("Network is required.");
        }

        if (string.IsNullOrWhiteSpace(TransactionId))
        {
            errors.Add("TransactionId is required.");
        }

        if (BlockNumber < 0)
        {
            errors.Add("BlockNumber cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(AnchoredHash))
        {
            errors.Add("AnchoredHash is required.");
        }

        if (Confirmations.HasValue && Confirmations.Value < 0)
        {
            errors.Add("Confirmations cannot be negative.");
        }

        return errors;
    }

    /// <summary>
    /// Creates a deep copy of this blockchain anchor reference.
    /// </summary>
    public BlockchainAnchorReference Clone()
    {
        return new BlockchainAnchorReference
        {
            Network = Network,
            TransactionId = TransactionId,
            BlockNumber = BlockNumber,
            BlockHash = BlockHash,
            BlockTimestamp = BlockTimestamp,
            AnchoredHash = AnchoredHash,
            ContractAddress = ContractAddress,
            Confirmations = Confirmations,
            ProviderMetadata = ProviderMetadata != null ? new Dictionary<string, object>(ProviderMetadata) : null
        };
    }
}

/// <summary>
/// Tracks orphaned WORM data from failed transactions.
/// When a transaction fails after WORM backup is written, we cannot roll back the WORM
/// (it's immutable). Instead, we track it as orphaned for cleanup after retention expires.
/// </summary>
public class OrphanedWormRecord
{
    /// <summary>
    /// Unique identifier for this orphaned WORM record.
    /// </summary>
    public required Guid OrphanId { get; init; }

    /// <summary>
    /// Object ID that this WORM backup was intended for.
    /// May be Guid.Empty if transaction failed before ID was assigned.
    /// </summary>
    public Guid? IntendedObjectId { get; init; }

    /// <summary>
    /// WORM storage reference for the orphaned backup.
    /// </summary>
    public required WormReference WormReference { get; init; }

    /// <summary>
    /// Status of this orphaned record.
    /// </summary>
    public required OrphanedWormStatus Status { get; init; }

    /// <summary>
    /// UTC timestamp when the orphaned record was created.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Reason for the transaction failure.
    /// </summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Write context from the failed transaction.
    /// </summary>
    public WriteContextRecord? OriginalWriteContext { get; init; }

    /// <summary>
    /// UTC timestamp when this record was reviewed by an administrator.
    /// </summary>
    public DateTimeOffset? ReviewedAt { get; init; }

    /// <summary>
    /// Administrator who reviewed this record.
    /// </summary>
    public string? ReviewedBy { get; init; }

    /// <summary>
    /// Notes from the administrative review.
    /// </summary>
    public string? ReviewNotes { get; init; }

    /// <summary>
    /// Validates the orphaned WORM record.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (OrphanId == Guid.Empty)
        {
            errors.Add("OrphanId cannot be empty.");
        }

        if (WormReference == null)
        {
            errors.Add("WormReference is required.");
        }
        else
        {
            var wormErrors = WormReference.Validate();
            errors.AddRange(wormErrors.Select(e => $"WormReference: {e}"));
        }

        if (Status == OrphanedWormStatus.Reviewed && string.IsNullOrWhiteSpace(ReviewedBy))
        {
            errors.Add("ReviewedBy is required when Status is Reviewed.");
        }

        return errors;
    }

    /// <summary>
    /// Creates a deep copy of this orphaned WORM record.
    /// </summary>
    public OrphanedWormRecord Clone()
    {
        return new OrphanedWormRecord
        {
            OrphanId = OrphanId,
            IntendedObjectId = IntendedObjectId,
            WormReference = WormReference.Clone(),
            Status = Status,
            CreatedAt = CreatedAt,
            FailureReason = FailureReason,
            OriginalWriteContext = OriginalWriteContext,
            ReviewedAt = ReviewedAt,
            ReviewedBy = ReviewedBy,
            ReviewNotes = ReviewNotes
        };
    }
}
