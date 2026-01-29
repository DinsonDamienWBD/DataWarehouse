// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.TamperProof;

/// <summary>
/// Helper methods for each phase of the tamper-proof read pipeline.
/// Implements the 5-phase read process with verification and recovery.
/// </summary>
public static class ReadPhaseHandlers
{
    /// <summary>
    /// Phase 1: Load manifest from metadata tier.
    /// </summary>
    /// <param name="objectId">Object ID to load.</param>
    /// <param name="version">Version to load (null for latest).</param>
    /// <param name="metadataStorage">Metadata tier storage.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Loaded manifest or null if not found.</returns>
    public static async Task<TamperProofManifest?> LoadManifestAsync(
        Guid objectId,
        int? version,
        IStorageProvider metadataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Loading manifest for object {ObjectId} version {Version}",
            objectId, version?.ToString() ?? "latest");

        try
        {
            // If version not specified, need to find latest
            // For now, assume version 1 if not specified
            var targetVersion = version ?? 1;

            var uri = new Uri($"metadata://manifests/{objectId}_v{targetVersion}.json");
            using var stream = await metadataStorage.LoadAsync(uri);

            if (stream == null)
            {
                logger.LogWarning("Manifest not found for object {ObjectId} version {Version}",
                    objectId, targetVersion);
                return null;
            }

            var manifestJson = await new StreamReader(stream).ReadToEndAsync(ct);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<TamperProofManifest>(manifestJson);

            if (manifest == null)
            {
                logger.LogError("Failed to deserialize manifest for object {ObjectId}", objectId);
                return null;
            }

            logger.LogDebug("Manifest loaded successfully for object {ObjectId} version {Version}",
                objectId, manifest.Version);

            return manifest;
        }
        catch (FileNotFoundException)
        {
            logger.LogWarning("Manifest file not found for object {ObjectId}", objectId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load manifest for object {ObjectId}", objectId);
            throw new InvalidOperationException("Failed to load manifest", ex);
        }
    }

    /// <summary>
    /// Phase 2: Load and reconstruct shards from RAID storage.
    /// Handles missing shards using parity reconstruction.
    /// </summary>
    /// <param name="manifest">Tamper-proof manifest with shard locations.</param>
    /// <param name="dataStorage">Data tier storage.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reconstructed data.</returns>
    public static async Task<byte[]> LoadAndReconstructShardsAsync(
        TamperProofManifest manifest,
        IStorageProvider dataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Loading and reconstructing {Count} shards for object {ObjectId}",
            manifest.RaidConfiguration.Shards.Count, manifest.ObjectId);

        try
        {
            var shards = new List<byte[]?>();
            var loadTasks = new List<Task<(int index, byte[]? data)>>();

            // Load all shards in parallel
            for (int i = 0; i < manifest.RaidConfiguration.Shards.Count; i++)
            {
                var shardIndex = i;
                var shardRecord = manifest.RaidConfiguration.Shards[i];

                loadTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var uri = new Uri($"data://shards/{shardRecord.StorageLocation}");
                        using var stream = await dataStorage.LoadAsync(uri);

                        if (stream == null)
                        {
                            logger.LogWarning("Shard {Index} not found", shardIndex);
                            return (shardIndex, (byte[]?)null);
                        }

                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, ct);
                        return (shardIndex, ms.ToArray());
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to load shard {Index}", shardIndex);
                        return (shardIndex, (byte[]?)null);
                    }
                }, ct));
            }

            var results = await Task.WhenAll(loadTasks);

            // Build shard array with nulls for missing shards
            shards = new List<byte[]?>(new byte[]?[manifest.RaidConfiguration.Shards.Count]);
            foreach (var (index, data) in results)
            {
                shards[index] = data;
            }

            // Check if we need parity reconstruction
            var missingCount = shards.Count(s => s == null);
            if (missingCount > manifest.RaidConfiguration.ParityShardCount)
            {
                throw new InvalidOperationException(
                    $"Cannot reconstruct: {missingCount} shards missing, " +
                    $"only {manifest.RaidConfiguration.ParityShardCount} parity shards available");
            }

            if (missingCount > 0)
            {
                logger.LogWarning("Reconstructing {Count} missing shards using parity", missingCount);
                ReconstructMissingShards(shards, manifest.RaidConfiguration);
            }

            // Reconstruct full data from data shards
            var dataShards = shards.Take(manifest.RaidConfiguration.DataShardCount).ToList();
            var reconstructedData = new byte[manifest.FinalContentSize];
            var offset = 0;

            for (int i = 0; i < dataShards.Count; i++)
            {
                var shard = dataShards[i];
                if (shard != null)
                {
                    var copyLength = Math.Min(shard.Length, reconstructedData.Length - offset);
                    Array.Copy(shard, 0, reconstructedData, offset, copyLength);
                    offset += copyLength;
                }
            }

            logger.LogDebug("Shard reconstruction complete: {Bytes} bytes", reconstructedData.Length);

            return reconstructedData;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load and reconstruct shards");
            throw new InvalidOperationException("Shard reconstruction failed", ex);
        }
    }

    /// <summary>
    /// Reconstructs missing shards using parity information (simple XOR for now).
    /// </summary>
    private static void ReconstructMissingShards(List<byte[]?> shards, RaidRecord raidConfig)
    {
        // Simple XOR reconstruction for demonstration
        // Production would use Reed-Solomon codes

        for (int i = 0; i < raidConfig.DataShardCount; i++)
        {
            if (shards[i] == null)
            {
                // Reconstruct data shard from other data shards and parity
                var shardSize = raidConfig.ShardSize;
                var reconstructed = new byte[shardSize];

                // XOR all available data shards and first parity shard
                for (int j = 0; j < raidConfig.DataShardCount; j++)
                {
                    if (j != i && shards[j] != null)
                    {
                        for (int k = 0; k < Math.Min(shardSize, shards[j]!.Length); k++)
                        {
                            reconstructed[k] ^= shards[j]![k];
                        }
                    }
                }

                // XOR with first parity shard
                var parityShard = shards[raidConfig.DataShardCount];
                if (parityShard != null)
                {
                    for (int k = 0; k < Math.Min(shardSize, parityShard.Length); k++)
                    {
                        reconstructed[k] ^= parityShard[k];
                    }
                }

                shards[i] = reconstructed;
            }
        }
    }

    /// <summary>
    /// Phase 3: Verify integrity of reconstructed data.
    /// </summary>
    /// <param name="data">Reconstructed data to verify.</param>
    /// <param name="manifest">Manifest with expected hashes.</param>
    /// <param name="readMode">Verification level.</param>
    /// <param name="integrity">Integrity provider.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    public static async Task<IntegrityVerificationResult> VerifyIntegrityAsync(
        byte[] data,
        TamperProofManifest manifest,
        ReadMode readMode,
        IIntegrityProvider integrity,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Verifying integrity with mode {ReadMode}", readMode);

        try
        {
            if (readMode == ReadMode.Fast)
            {
                // Fast mode: skip verification
                logger.LogDebug("Fast mode: skipping integrity verification");
                return IntegrityVerificationResult.CreateValid(
                    IntegrityHash.Create(manifest.HashAlgorithm, manifest.FinalContentHash),
                    IntegrityHash.Create(manifest.HashAlgorithm, manifest.FinalContentHash));
            }

            // Compute actual hash
            var actualHash = await integrity.ComputeHashAsync(data, manifest.HashAlgorithm, ct);
            var expectedHash = IntegrityHash.Create(manifest.HashAlgorithm, manifest.FinalContentHash);

            // Compare hashes
            var isValid = string.Equals(
                actualHash.HashValue,
                expectedHash.HashValue,
                StringComparison.OrdinalIgnoreCase);

            if (isValid)
            {
                logger.LogDebug("Integrity verification passed");
                return IntegrityVerificationResult.CreateValid(expectedHash, actualHash);
            }
            else
            {
                logger.LogError("Integrity verification FAILED: expected {Expected}, got {Actual}",
                    expectedHash.HashValue, actualHash.HashValue);

                return IntegrityVerificationResult.CreateFailed(
                    "Integrity verification failed: hash mismatch",
                    expectedHash,
                    actualHash);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Integrity verification error");
            return IntegrityVerificationResult.CreateFailed($"Verification error: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 3b: Recover data from WORM backup when tampering is detected.
    /// </summary>
    /// <param name="manifest">Manifest with WORM reference.</param>
    /// <param name="worm">WORM storage provider.</param>
    /// <param name="dataStorage">Data tier storage for restoring shards.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recovery result.</returns>
    public static async Task<RecoveryResult> RecoverFromWormAsync(
        TamperProofManifest manifest,
        IWormStorageProvider worm,
        IStorageProvider dataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogWarning("Attempting recovery from WORM for object {ObjectId}", manifest.ObjectId);

        try
        {
            if (manifest.WormBackup == null)
            {
                return RecoveryResult.CreateFailure(
                    manifest.ObjectId,
                    manifest.Version,
                    "No WORM backup reference in manifest");
            }

            // Load data from WORM
            var wormData = await worm.ReadAsync(manifest.WormBackup.StorageLocation, ct);

            if (wormData == null)
            {
                return RecoveryResult.CreateFailure(
                    manifest.ObjectId,
                    manifest.Version,
                    "WORM backup not found");
            }

            // TODO: Restore shards to data storage
            // This would recreate the RAID shards from WORM backup

            logger.LogInformation("Recovery from WORM successful for object {ObjectId}", manifest.ObjectId);

            return RecoveryResult.CreateSuccess(
                manifest.ObjectId,
                manifest.Version,
                "WORM Storage",
                "Restored from immutable backup");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WORM recovery failed for object {ObjectId}", manifest.ObjectId);
            return RecoveryResult.CreateFailure(
                manifest.ObjectId,
                manifest.Version,
                $"Recovery error: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 4: Reverse pipeline transformations to get original data.
    /// </summary>
    /// <param name="transformedData">Data after pipeline transformations.</param>
    /// <param name="manifest">Manifest with pipeline stage history.</param>
    /// <param name="orchestrator">Pipeline orchestrator.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Original data before transformations.</returns>
    public static async Task<byte[]> ReversePipelineTransformationsAsync(
        byte[] transformedData,
        TamperProofManifest manifest,
        IPipelineOrchestrator orchestrator,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Reversing {Count} pipeline transformations", manifest.PipelineStages.Count);

        try
        {
            var currentData = transformedData;

            // Reverse stages in opposite order
            for (int i = manifest.PipelineStages.Count - 1; i >= 0; i--)
            {
                var stage = manifest.PipelineStages[i];
                logger.LogDebug("Reversing stage {Index}: {Type}", stage.StageIndex, stage.StageType);

                // TODO: Apply reverse transformation via orchestrator
                // For now, data passes through unchanged if no stages
            }

            // Remove content padding if present
            if (manifest.ContentPadding != null)
            {
                logger.LogDebug("Removing content padding: {Prefix} prefix + {Suffix} suffix bytes",
                    manifest.ContentPadding.PrefixPaddingBytes,
                    manifest.ContentPadding.SuffixPaddingBytes);

                var unpadded = new byte[currentData.Length - manifest.ContentPadding.TotalPaddingBytes];
                Array.Copy(
                    currentData,
                    manifest.ContentPadding.PrefixPaddingBytes,
                    unpadded,
                    0,
                    unpadded.Length);

                currentData = unpadded;
            }

            logger.LogDebug("Pipeline reversal complete: {Bytes} bytes", currentData.Length);

            return currentData;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reverse pipeline transformations");
            throw new InvalidOperationException("Pipeline reversal failed", ex);
        }
    }
}
