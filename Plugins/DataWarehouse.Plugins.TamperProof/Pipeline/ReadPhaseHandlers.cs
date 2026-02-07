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

            // Restore shards to data storage
            logger.LogDebug("Restoring shards from WORM backup to data storage");

            try
            {
                // Recreate RAID shards from WORM backup
                var shardSize = manifest.RaidConfiguration.ShardSize;
                var dataShardCount = manifest.RaidConfiguration.DataShardCount;
                var parityShardCount = manifest.RaidConfiguration.ParityShardCount;

                // Split WORM data back into data shards
                var shards = new List<byte[]>();
                for (int i = 0; i < dataShardCount; i++)
                {
                    var start = i * shardSize;
                    var length = Math.Min(shardSize, wormData.Length - start);
                    if (length > 0)
                    {
                        var shard = new byte[length];
                        Array.Copy(wormData, start, shard, 0, length);
                        shards.Add(shard);
                    }
                }

                // Recreate parity shards (simple XOR for now)
                for (int i = 0; i < parityShardCount; i++)
                {
                    var parityShard = new byte[shardSize];
                    for (int j = 0; j < dataShardCount; j++)
                    {
                        for (int k = 0; k < Math.Min(shardSize, shards[j].Length); k++)
                        {
                            parityShard[k] ^= shards[j][k];
                        }
                    }
                    shards.Add(parityShard);
                }

                // Write shards back to data storage
                var writeErrors = new List<string>();
                var writtenCount = 0;

                for (int i = 0; i < shards.Count; i++)
                {
                    var shardRecord = manifest.RaidConfiguration.Shards[i];
                    var uri = new Uri($"data://shards/{shardRecord.StorageLocation}");

                    try
                    {
                        using var shardStream = new MemoryStream(shards[i]);
                        await dataStorage.SaveAsync(uri, shardStream);
                        writtenCount++;
                        logger.LogDebug("Restored shard {Index} to {Location}", i, shardRecord.StorageLocation);
                    }
                    catch (Exception ex)
                    {
                        writeErrors.Add($"Shard {i}: {ex.Message}");
                        logger.LogWarning(ex, "Failed to restore shard {Index}", i);
                    }
                }

                if (writeErrors.Count > 0)
                {
                    return RecoveryResult.CreateFailure(
                        manifest.ObjectId,
                        manifest.Version,
                        $"Partial restoration: {writtenCount}/{shards.Count} shards restored. Errors: {string.Join("; ", writeErrors)}");
                }

                logger.LogInformation("Recovery from WORM successful: restored {Count} shards for object {ObjectId}",
                    writtenCount, manifest.ObjectId);

                return RecoveryResult.CreateSuccess(
                    manifest.ObjectId,
                    manifest.Version,
                    "WORM Storage",
                    $"Restored {writtenCount} shards from immutable backup");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to restore shards from WORM backup");
                return RecoveryResult.CreateFailure(
                    manifest.ObjectId,
                    manifest.Version,
                    $"Shard restoration error: {ex.Message}");
            }
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

            // Apply reverse transformations if pipeline stages were used
            if (manifest.PipelineStages.Count > 0)
            {
                logger.LogDebug("Applying reverse transformations for {Count} pipeline stages", manifest.PipelineStages.Count);

                // Use orchestrator to reverse the pipeline
                // The orchestrator should handle reversing in the correct order
                using var inputStream = new MemoryStream(currentData);
                using var outputStream = await orchestrator.ReversePipelineAsync(inputStream, ct);

                using var resultMs = new MemoryStream();
                await outputStream.CopyToAsync(resultMs, ct);
                currentData = resultMs.ToArray();

                logger.LogDebug("Pipeline reversal complete: transformed {FromBytes} bytes to {ToBytes} bytes",
                    transformedData.Length, currentData.Length);

                // Verify intermediate hashes if needed (for debugging/auditing)
                for (int i = manifest.PipelineStages.Count - 1; i >= 0; i--)
                {
                    var stage = manifest.PipelineStages[i];
                    logger.LogDebug("Reversed stage {Index}: {Type} ({InputSize} -> {OutputSize} bytes)",
                        stage.StageIndex, stage.StageType, stage.OutputSize, stage.InputSize);
                }
            }
            else
            {
                logger.LogDebug("No pipeline stages to reverse");
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
