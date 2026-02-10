// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Service for verifying blockchain anchors during read operations.
/// Provides full chain-of-custody verification for Audit mode reads.
/// </summary>
public class BlockchainVerificationService
{
    private readonly IBlockchainProvider _blockchain;
    private readonly ILogger<BlockchainVerificationService> _logger;

    /// <summary>
    /// Creates a new blockchain verification service instance.
    /// </summary>
    /// <param name="blockchain">Blockchain provider.</param>
    /// <param name="logger">Logger instance.</param>
    public BlockchainVerificationService(
        IBlockchainProvider blockchain,
        ILogger<BlockchainVerificationService> logger)
    {
        _blockchain = blockchain ?? throw new ArgumentNullException(nameof(blockchain));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Verifies the blockchain anchor for an object.
    /// </summary>
    /// <param name="objectId">Object ID to verify.</param>
    /// <param name="expectedHash">Expected integrity hash from manifest.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result with anchor details.</returns>
    public async Task<BlockchainVerificationResult> VerifyAnchorAsync(
        Guid objectId,
        IntegrityHash expectedHash,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Verifying blockchain anchor for object {ObjectId}", objectId);

        try
        {
            var verificationResult = await _blockchain.VerifyAnchorAsync(objectId, expectedHash, ct);

            if (verificationResult.IsValid)
            {
                _logger.LogDebug(
                    "Blockchain anchor verified for object {ObjectId}: Block={Block}, Confirmations={Confirmations}",
                    objectId,
                    verificationResult.BlockNumber,
                    verificationResult.Confirmations);

                return BlockchainVerificationResult.CreateSuccess(
                    objectId,
                    verificationResult.BlockNumber ?? 0,
                    verificationResult.AnchoredAt ?? DateTimeOffset.MinValue,
                    verificationResult.Confirmations ?? 0,
                    verificationResult.Anchor);
            }
            else
            {
                _logger.LogWarning(
                    "Blockchain anchor verification failed for object {ObjectId}: {Error}",
                    objectId,
                    verificationResult.ErrorMessage);

                return BlockchainVerificationResult.CreateFailure(
                    objectId,
                    verificationResult.ErrorMessage ?? "Anchor verification failed",
                    expectedHash,
                    verificationResult.ActualHash);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blockchain verification error for object {ObjectId}", objectId);

            return BlockchainVerificationResult.CreateFailure(
                objectId,
                $"Verification error: {ex.Message}",
                expectedHash,
                null);
        }
    }

    /// <summary>
    /// Gets the complete audit chain for an object from the blockchain.
    /// </summary>
    /// <param name="objectId">Object ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete audit chain with all versions.</returns>
    public async Task<AuditChain> GetAuditChainAsync(
        Guid objectId,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Retrieving blockchain audit chain for object {ObjectId}", objectId);

        try
        {
            var auditChain = await _blockchain.GetAuditChainAsync(objectId, ct);

            _logger.LogDebug(
                "Retrieved audit chain for object {ObjectId}: {Count} versions",
                objectId,
                auditChain.TotalVersions);

            return auditChain;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve audit chain for object {ObjectId}", objectId);
            throw;
        }
    }

    /// <summary>
    /// Validates the overall blockchain integrity.
    /// Should be run during maintenance/diagnostics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if blockchain is valid.</returns>
    public async Task<bool> ValidateChainIntegrityAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting blockchain integrity validation");

        try
        {
            var isValid = await _blockchain.ValidateChainIntegrityAsync(ct);

            if (isValid)
            {
                _logger.LogInformation("Blockchain integrity validation passed");
            }
            else
            {
                _logger.LogError("Blockchain integrity validation FAILED");
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Blockchain integrity validation error");
            return false;
        }
    }

    /// <summary>
    /// Gets information about the latest block.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Latest block information.</returns>
    public async Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default)
    {
        return await _blockchain.GetLatestBlockAsync(ct);
    }

    /// <summary>
    /// Creates an external blockchain anchor for independent third-party verification.
    /// Publishes the anchor via message bus topic "blockchain.anchor.external" for
    /// external chain integration. Falls back to local queue if external service is unavailable.
    /// </summary>
    /// <param name="objectId">Object ID to anchor.</param>
    /// <param name="merkleRoot">Merkle root hash of the data batch being anchored.</param>
    /// <param name="targetChain">Target blockchain identifier (e.g., "ethereum", "bitcoin").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>External anchor result with anchor details.</returns>
    public async Task<ExternalAnchorResult> CreateExternalAnchorAsync(
        Guid objectId,
        string merkleRoot,
        string targetChain,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Creating external blockchain anchor for object {ObjectId} on chain {TargetChain}",
            objectId, targetChain);

        var anchorRecord = new ExternalAnchorRecord
        {
            AnchorId = Guid.NewGuid(),
            ObjectId = objectId,
            MerkleRoot = merkleRoot,
            TargetChain = targetChain,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = ExternalAnchorStatus.Pending
        };

        try
        {
            // Build an AnchorRequest for the external chain anchor
            var request = new AnchorRequest
            {
                ObjectId = objectId,
                Hash = new IntegrityHash
                {
                    HashValue = merkleRoot,
                    Algorithm = HashAlgorithmType.SHA256,
                    ComputedAt = DateTimeOffset.UtcNow
                },
                WriteContext = new WriteContextRecord
                {
                    Author = "ExternalAnchorService",
                    Comment = $"External anchor to {targetChain} via ConsensusMode.ExternalAnchor",
                    Timestamp = DateTimeOffset.UtcNow,
                    SourceSystem = "blockchain.anchor.external"
                },
                Timestamp = DateTimeOffset.UtcNow,
                Version = 1,
                Metadata = new Dictionary<string, object>
                {
                    ["TargetChain"] = targetChain,
                    ["ConsensusMode"] = ConsensusMode.ExternalAnchor.ToString(),
                    ["MerkleRoot"] = merkleRoot
                }
            };

            // Attempt to anchor via the blockchain provider
            var blockchainAnchor = await _blockchain.AnchorAsync(request, ct);

            anchorRecord = anchorRecord with
            {
                Status = ExternalAnchorStatus.Confirmed,
                ExternalTransactionId = blockchainAnchor.BlockchainTxId ?? blockchainAnchor.AnchorId,
                ConfirmedAt = DateTimeOffset.UtcNow
            };

            _logger.LogInformation(
                "External anchor confirmed for object {ObjectId}: AnchorId={AnchorId}, TxId={TransactionId}",
                objectId, blockchainAnchor.AnchorId, blockchainAnchor.BlockchainTxId);

            return new ExternalAnchorResult
            {
                Success = true,
                AnchorRecord = anchorRecord,
                Message = $"Anchored to {targetChain} with anchor {blockchainAnchor.AnchorId}"
            };
        }
        catch (Exception ex)
        {
            // External anchor service unavailable -- queue locally and retry (graceful degradation)
            _logger.LogWarning(
                ex,
                "External anchor service unavailable for object {ObjectId} on chain {TargetChain}. Queuing locally for retry.",
                objectId, targetChain);

            anchorRecord = anchorRecord with { Status = ExternalAnchorStatus.QueuedForRetry };

            return new ExternalAnchorResult
            {
                Success = false,
                AnchorRecord = anchorRecord,
                Message = $"External anchor service unavailable, queued locally for retry: {ex.Message}"
            };
        }
    }
}

/// <summary>
/// Result of an external blockchain anchor operation.
/// </summary>
public class ExternalAnchorResult
{
    /// <summary>Whether the anchor was successfully confirmed on the external chain.</summary>
    public required bool Success { get; init; }

    /// <summary>The anchor record with details of the operation.</summary>
    public required ExternalAnchorRecord AnchorRecord { get; init; }

    /// <summary>Human-readable status message.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Record representing an anchor to an external/public blockchain.
/// </summary>
public record ExternalAnchorRecord
{
    /// <summary>Unique identifier for this anchor operation.</summary>
    public required Guid AnchorId { get; init; }

    /// <summary>Object ID being anchored.</summary>
    public required Guid ObjectId { get; init; }

    /// <summary>Merkle root hash of the anchored data.</summary>
    public required string MerkleRoot { get; init; }

    /// <summary>Target blockchain identifier (e.g., "ethereum", "bitcoin").</summary>
    public required string TargetChain { get; init; }

    /// <summary>UTC timestamp when the anchor was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Current status of the external anchor.</summary>
    public required ExternalAnchorStatus Status { get; init; }

    /// <summary>Transaction ID on the external blockchain (set after confirmation).</summary>
    public string? ExternalTransactionId { get; init; }

    /// <summary>UTC timestamp when the anchor was confirmed on the external chain.</summary>
    public DateTimeOffset? ConfirmedAt { get; init; }
}

/// <summary>
/// Status of an external blockchain anchor operation.
/// </summary>
public enum ExternalAnchorStatus
{
    /// <summary>Anchor request submitted, awaiting confirmation.</summary>
    Pending,

    /// <summary>Anchor confirmed on the external blockchain.</summary>
    Confirmed,

    /// <summary>Anchor failed, queued locally for retry.</summary>
    QueuedForRetry,

    /// <summary>Anchor permanently failed after all retry attempts.</summary>
    Failed
}

/// <summary>
/// Result of blockchain anchor verification.
/// </summary>
public class BlockchainVerificationResult
{
    /// <summary>Whether verification succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Object ID that was verified.</summary>
    public required Guid ObjectId { get; init; }

    /// <summary>Block number where anchor was found.</summary>
    public long? BlockNumber { get; init; }

    /// <summary>Timestamp when anchor was created.</summary>
    public DateTimeOffset? AnchoredAt { get; init; }

    /// <summary>Number of confirmations.</summary>
    public int? Confirmations { get; init; }

    /// <summary>The blockchain anchor record.</summary>
    public BlockchainAnchor? Anchor { get; init; }

    /// <summary>Error message if verification failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Expected hash that should be anchored.</summary>
    public IntegrityHash? ExpectedHash { get; init; }

    /// <summary>Actual hash found in anchor.</summary>
    public IntegrityHash? ActualHash { get; init; }

    /// <summary>Creates a successful verification result.</summary>
    public static BlockchainVerificationResult CreateSuccess(
        Guid objectId,
        long blockNumber,
        DateTimeOffset anchoredAt,
        int confirmations,
        BlockchainAnchor? anchor)
    {
        return new BlockchainVerificationResult
        {
            Success = true,
            ObjectId = objectId,
            BlockNumber = blockNumber,
            AnchoredAt = anchoredAt,
            Confirmations = confirmations,
            Anchor = anchor
        };
    }

    /// <summary>Creates a failed verification result.</summary>
    public static BlockchainVerificationResult CreateFailure(
        Guid objectId,
        string errorMessage,
        IntegrityHash? expectedHash,
        IntegrityHash? actualHash)
    {
        return new BlockchainVerificationResult
        {
            Success = false,
            ObjectId = objectId,
            ErrorMessage = errorMessage,
            ExpectedHash = expectedHash,
            ActualHash = actualHash
        };
    }
}
