// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.Plugins.TamperProof.Services;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.TamperProof;

/// <summary>
/// Helper methods for each phase of the tamper-proof read pipeline.
/// Implements the 5-phase read process with verification and recovery.
/// </summary>
public static class ReadPhaseHandlers
{
    /// <summary>
    /// Gets the seal status for a block, to be included in read metadata.
    /// </summary>
    /// <param name="objectId">Object/block ID to check.</param>
    /// <param name="sealService">Seal service instance.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Seal information if sealed, null otherwise.</returns>
    public static async Task<SealStatusInfo?> GetSealStatusAsync(
        Guid objectId,
        ISealService? sealService,
        ILogger logger,
        CancellationToken ct)
    {
        if (sealService == null)
        {
            return null;
        }

        try
        {
            var sealInfo = await sealService.GetSealInfoAsync(objectId, ct);
            if (sealInfo == null)
            {
                return null;
            }

            logger.LogDebug(
                "Block {ObjectId} is sealed since {SealedAt}. Reason: {Reason}",
                objectId, sealInfo.SealedAt, sealInfo.Reason);

            return new SealStatusInfo
            {
                IsSealed = true,
                SealedAt = sealInfo.SealedAt,
                Reason = sealInfo.Reason,
                SealedBy = sealInfo.SealedBy,
                SealToken = sealInfo.SealToken
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get seal status for block {ObjectId}", objectId);
            return null;
        }
    }

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
            int targetVersion;

            if (version.HasValue)
            {
                targetVersion = version.Value;
            }
            else
            {
                // Find the latest version by checking version index or scanning
                targetVersion = await FindLatestVersionAsync(objectId, metadataStorage, logger, ct);
                if (targetVersion == 0)
                {
                    logger.LogWarning("No versions found for object {ObjectId}", objectId);
                    return null;
                }
            }

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
    /// Finds the latest version number for an object by checking version index.
    /// </summary>
    /// <param name="objectId">Object ID to query.</param>
    /// <param name="metadataStorage">Metadata storage provider.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Latest version number, or 0 if none found.</returns>
    private static async Task<int> FindLatestVersionAsync(
        Guid objectId,
        IStorageProvider metadataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            // Try to load version index first
            var indexUri = new Uri($"metadata://manifests/{objectId}_versions.json");
            try
            {
                using var indexStream = await metadataStorage.LoadAsync(indexUri);
                if (indexStream != null)
                {
                    var indexJson = await new StreamReader(indexStream).ReadToEndAsync(ct);
                    var versionIndex = System.Text.Json.JsonSerializer.Deserialize<VersionIndex>(indexJson);
                    if (versionIndex != null)
                    {
                        return versionIndex.LatestVersion;
                    }
                }
            }
            catch
            {
                // Index doesn't exist, fall back to scanning
            }

            // Fall back to scanning versions starting from 1
            var latestVersion = 0;
            var maxVersionsToScan = 1000; // Safety limit

            for (int v = 1; v <= maxVersionsToScan; v++)
            {
                var manifestUri = new Uri($"metadata://manifests/{objectId}_v{v}.json");
                try
                {
                    using var stream = await metadataStorage.LoadAsync(manifestUri);
                    if (stream == null)
                    {
                        // No more versions
                        break;
                    }
                    latestVersion = v;
                }
                catch (FileNotFoundException)
                {
                    break;
                }
                catch
                {
                    // Continue checking in case of transient errors
                }
            }

            // If we found versions, update the index for future lookups
            if (latestVersion > 0)
            {
                try
                {
                    var versionIndex = new VersionIndex
                    {
                        ObjectId = objectId,
                        LatestVersion = latestVersion,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    var indexJson = System.Text.Json.JsonSerializer.Serialize(versionIndex);
                    using var indexMs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(indexJson));
                    await metadataStorage.SaveAsync(indexUri, indexMs);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to update version index for object {ObjectId}", objectId);
                    // Non-critical, continue
                }
            }

            return latestVersion;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error finding latest version for object {ObjectId}, defaulting to version 1", objectId);
            return 1; // Default to version 1
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
            var loadTasks = new List<Task<ShardLoadResult>>();

            // Load all shards in parallel with integrity verification
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
                            logger.LogWarning("Shard {Index} not found at {Location}", shardIndex, shardRecord.StorageLocation);
                            return new ShardLoadResult
                            {
                                ShardIndex = shardIndex,
                                Data = null,
                                LoadedSuccessfully = false,
                                IntegrityValid = false,
                                ErrorMessage = "Shard not found"
                            };
                        }

                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, ct);
                        var shardData = ms.ToArray();

                        // Verify shard integrity using stored hash
                        // TODO: Add bus delegation with SHA256 fallback (static method - refactor to instance method for MessageBus access)
                        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(shardData));
                        var expectedHash = shardRecord.ContentHash;
                        var integrityValid = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);

                        if (!integrityValid)
                        {
                            logger.LogWarning("Shard {Index} integrity check failed: expected {Expected}, got {Actual}",
                                shardIndex, expectedHash, actualHash);
                        }

                        return new ShardLoadResult
                        {
                            ShardIndex = shardIndex,
                            Data = shardData,
                            LoadedSuccessfully = true,
                            IntegrityValid = integrityValid,
                            ExpectedHash = expectedHash,
                            ActualHash = actualHash
                        };
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to load shard {Index}", shardIndex);
                        return new ShardLoadResult
                        {
                            ShardIndex = shardIndex,
                            Data = null,
                            LoadedSuccessfully = false,
                            IntegrityValid = false,
                            ErrorMessage = ex.Message
                        };
                    }
                }, ct));
            }

            var results = await Task.WhenAll(loadTasks);

            // Build shard array, treating corrupted shards as missing
            shards = new List<byte[]?>(new byte[]?[manifest.RaidConfiguration.Shards.Count]);
            var corruptedShards = new List<int>();
            var missingShards = new List<int>();

            foreach (var result in results)
            {
                if (result.LoadedSuccessfully && result.IntegrityValid)
                {
                    shards[result.ShardIndex] = result.Data;
                }
                else if (result.LoadedSuccessfully && !result.IntegrityValid)
                {
                    corruptedShards.Add(result.ShardIndex);
                    logger.LogWarning("Shard {Index} corrupted, will attempt reconstruction", result.ShardIndex);
                }
                else
                {
                    missingShards.Add(result.ShardIndex);
                }
            }

            // Total shards needing reconstruction
            var needsReconstruction = corruptedShards.Count + missingShards.Count;

            if (needsReconstruction > manifest.RaidConfiguration.ParityShardCount)
            {
                throw new InvalidOperationException(
                    $"Cannot reconstruct: {needsReconstruction} shards unavailable " +
                    $"({corruptedShards.Count} corrupted, {missingShards.Count} missing), " +
                    $"only {manifest.RaidConfiguration.ParityShardCount} parity shards available for recovery");
            }

            if (needsReconstruction > 0)
            {
                logger.LogWarning("Reconstructing {Count} shards using parity (corrupted: {Corrupted}, missing: {Missing})",
                    needsReconstruction, corruptedShards.Count, missingShards.Count);
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
    /// Extended shard load operation that also returns shard verification results.
    /// </summary>
    /// <param name="manifest">Tamper-proof manifest.</param>
    /// <param name="dataStorage">Data storage provider.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Reconstructed data and shard verification details.</returns>
    public static async Task<ShardReconstructionResult> LoadAndReconstructShardsWithVerificationAsync(
        TamperProofManifest manifest,
        IStorageProvider dataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Loading and reconstructing {Count} shards with verification for object {ObjectId}",
            manifest.RaidConfiguration.Shards.Count, manifest.ObjectId);

        var shardResults = new List<ShardVerificationResult>();
        var shards = new List<byte[]?>(new byte[]?[manifest.RaidConfiguration.Shards.Count]);
        var corruptedShards = new List<int>();
        var missingShards = new List<int>();

        try
        {
            var loadTasks = new List<Task<ShardLoadResult>>();

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
                            return new ShardLoadResult
                            {
                                ShardIndex = shardIndex,
                                Data = null,
                                LoadedSuccessfully = false,
                                IntegrityValid = false,
                                ErrorMessage = "Shard not found"
                            };
                        }

                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, ct);
                        var shardData = ms.ToArray();

                        // TODO: Add bus delegation with SHA256 fallback (static method - refactor to instance method for MessageBus access)
                        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(shardData));
                        var expectedHash = shardRecord.ContentHash;
                        var integrityValid = string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);

                        return new ShardLoadResult
                        {
                            ShardIndex = shardIndex,
                            Data = shardData,
                            LoadedSuccessfully = true,
                            IntegrityValid = integrityValid,
                            ExpectedHash = expectedHash,
                            ActualHash = actualHash
                        };
                    }
                    catch (Exception ex)
                    {
                        return new ShardLoadResult
                        {
                            ShardIndex = shardIndex,
                            Data = null,
                            LoadedSuccessfully = false,
                            IntegrityValid = false,
                            ErrorMessage = ex.Message
                        };
                    }
                }, ct));
            }

            var loadResults = await Task.WhenAll(loadTasks);

            // Process results
            foreach (var result in loadResults)
            {
                var shardRecord = manifest.RaidConfiguration.Shards[result.ShardIndex];
                var expectedHash = IntegrityHash.Create(manifest.HashAlgorithm, shardRecord.ContentHash);
                var actualHash = result.ActualHash != null
                    ? IntegrityHash.Create(manifest.HashAlgorithm, result.ActualHash)
                    : null;

                if (result.LoadedSuccessfully && result.IntegrityValid)
                {
                    shards[result.ShardIndex] = result.Data;
                    shardResults.Add(ShardVerificationResult.CreateValid(result.ShardIndex, expectedHash, actualHash!));
                }
                else if (result.LoadedSuccessfully && !result.IntegrityValid)
                {
                    corruptedShards.Add(result.ShardIndex);
                    shardResults.Add(ShardVerificationResult.CreateFailed(
                        result.ShardIndex,
                        "Integrity verification failed",
                        expectedHash,
                        actualHash));
                }
                else
                {
                    missingShards.Add(result.ShardIndex);
                    shardResults.Add(ShardVerificationResult.CreateFailed(
                        result.ShardIndex,
                        result.ErrorMessage ?? "Shard unavailable"));
                }
            }

            // Check if reconstruction is possible
            var needsReconstruction = corruptedShards.Count + missingShards.Count;

            if (needsReconstruction > manifest.RaidConfiguration.ParityShardCount)
            {
                return new ShardReconstructionResult
                {
                    Success = false,
                    Data = null,
                    ShardResults = shardResults,
                    CorruptedShards = corruptedShards,
                    MissingShards = missingShards,
                    ReconstructedShards = new List<int>(),
                    ErrorMessage = $"Cannot reconstruct: {needsReconstruction} shards unavailable, " +
                                   $"only {manifest.RaidConfiguration.ParityShardCount} parity shards available"
                };
            }

            // Reconstruct if needed
            var reconstructedShardIndices = new List<int>();
            if (needsReconstruction > 0)
            {
                ReconstructMissingShards(shards, manifest.RaidConfiguration);
                reconstructedShardIndices.AddRange(corruptedShards);
                reconstructedShardIndices.AddRange(missingShards);
            }

            // Build final data
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

            return new ShardReconstructionResult
            {
                Success = true,
                Data = reconstructedData,
                ShardResults = shardResults,
                CorruptedShards = corruptedShards,
                MissingShards = missingShards,
                ReconstructedShards = reconstructedShardIndices
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load and reconstruct shards with verification");
            return new ShardReconstructionResult
            {
                Success = false,
                Data = null,
                ShardResults = shardResults,
                CorruptedShards = corruptedShards,
                MissingShards = missingShards,
                ReconstructedShards = new List<int>(),
                ErrorMessage = ex.Message
            };
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

    /// <summary>
    /// Phase 3c: Verify blockchain anchor for Audit mode reads.
    /// Provides full chain-of-custody verification.
    /// </summary>
    /// <param name="manifest">Tamper-proof manifest.</param>
    /// <param name="blockchainService">Blockchain verification service.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Blockchain verification result.</returns>
    public static async Task<BlockchainVerificationResult> VerifyBlockchainAnchorAsync(
        TamperProofManifest manifest,
        BlockchainVerificationService blockchainService,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Verifying blockchain anchor for object {ObjectId} version {Version}",
            manifest.ObjectId, manifest.Version);

        try
        {
            var expectedHash = IntegrityHash.Create(manifest.HashAlgorithm, manifest.FinalContentHash);
            var result = await blockchainService.VerifyAnchorAsync(manifest.ObjectId, expectedHash, ct);

            if (result.Success)
            {
                logger.LogDebug(
                    "Blockchain anchor verified: Block={Block}, Confirmations={Confirmations}",
                    result.BlockNumber,
                    result.Confirmations);
            }
            else
            {
                logger.LogWarning(
                    "Blockchain anchor verification failed for object {ObjectId}: {Error}",
                    manifest.ObjectId,
                    result.ErrorMessage);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Blockchain anchor verification error for object {ObjectId}", manifest.ObjectId);

            return BlockchainVerificationResult.CreateFailure(
                manifest.ObjectId,
                $"Verification error: {ex.Message}",
                IntegrityHash.Create(manifest.HashAlgorithm, manifest.FinalContentHash),
                null);
        }
    }

    /// <summary>
    /// Phase 3d: Get full audit chain from blockchain for comprehensive audit.
    /// </summary>
    /// <param name="objectId">Object ID to query.</param>
    /// <param name="blockchainService">Blockchain verification service.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete audit chain with all versions.</returns>
    public static async Task<AuditChain> GetAuditChainAsync(
        Guid objectId,
        BlockchainVerificationService blockchainService,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Retrieving audit chain for object {ObjectId}", objectId);

        try
        {
            var auditChain = await blockchainService.GetAuditChainAsync(objectId, ct);
            logger.LogDebug("Retrieved audit chain: {Versions} versions", auditChain.TotalVersions);
            return auditChain;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve audit chain for object {ObjectId}", objectId);
            throw;
        }
    }

    /// <summary>
    /// Performs comprehensive integrity verification including optional blockchain check.
    /// </summary>
    /// <param name="data">Reconstructed data to verify.</param>
    /// <param name="manifest">Manifest with expected hashes.</param>
    /// <param name="readMode">Verification level.</param>
    /// <param name="integrity">Integrity provider.</param>
    /// <param name="blockchainService">Optional blockchain service for Audit mode.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comprehensive verification result.</returns>
    public static Task<ComprehensiveVerificationResult> VerifyIntegrityWithBlockchainAsync(
        byte[] data,
        TamperProofManifest manifest,
        ReadMode readMode,
        IIntegrityProvider integrity,
        BlockchainVerificationService? blockchainService,
        ILogger logger,
        CancellationToken ct)
    {
        // Call overload without seal service for backward compatibility
        return VerifyIntegrityWithBlockchainAsync(
            data, manifest, readMode, integrity, blockchainService, null, logger, ct);
    }

    /// <summary>
    /// Performs comprehensive integrity verification including optional blockchain check and seal status.
    /// </summary>
    /// <param name="data">Reconstructed data to verify.</param>
    /// <param name="manifest">Manifest with expected hashes.</param>
    /// <param name="readMode">Verification level.</param>
    /// <param name="integrity">Integrity provider.</param>
    /// <param name="blockchainService">Optional blockchain service for Audit mode.</param>
    /// <param name="sealService">Optional seal service for seal status.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Comprehensive verification result including seal status.</returns>
    public static async Task<ComprehensiveVerificationResult> VerifyIntegrityWithBlockchainAsync(
        byte[] data,
        TamperProofManifest manifest,
        ReadMode readMode,
        IIntegrityProvider integrity,
        BlockchainVerificationService? blockchainService,
        ISealService? sealService,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Performing comprehensive verification with mode {ReadMode}", readMode);

        // First perform standard integrity verification
        var integrityResult = await VerifyIntegrityAsync(data, manifest, readMode, integrity, logger, ct);

        // For Audit mode, also verify blockchain anchor
        BlockchainVerificationResult? blockchainResult = null;
        if (readMode == ReadMode.Audit && blockchainService != null)
        {
            logger.LogDebug("Audit mode: verifying blockchain anchor");
            blockchainResult = await VerifyBlockchainAnchorAsync(manifest, blockchainService, logger, ct);
        }

        // Get seal status if seal service is available
        SealStatusInfo? sealStatus = null;
        if (sealService != null)
        {
            sealStatus = await GetSealStatusAsync(manifest.ObjectId, sealService, logger, ct);
        }

        return new ComprehensiveVerificationResult
        {
            IntegrityResult = integrityResult,
            BlockchainResult = blockchainResult,
            ReadMode = readMode,
            OverallValid = integrityResult.IntegrityValid &&
                          (readMode != ReadMode.Audit || blockchainResult?.Success == true),
            SealStatus = sealStatus
        };
    }
}

/// <summary>
/// Result of comprehensive verification including both integrity and blockchain checks.
/// </summary>
public class ComprehensiveVerificationResult
{
    /// <summary>Result of the integrity hash verification.</summary>
    public required IntegrityVerificationResult IntegrityResult { get; init; }

    /// <summary>Result of blockchain anchor verification (Audit mode only).</summary>
    public BlockchainVerificationResult? BlockchainResult { get; init; }

    /// <summary>The read mode used for verification.</summary>
    public required ReadMode ReadMode { get; init; }

    /// <summary>Whether all verification checks passed.</summary>
    public required bool OverallValid { get; init; }

    /// <summary>Seal status of the block (T4.5 feature).</summary>
    public SealStatusInfo? SealStatus { get; init; }

    /// <summary>Gets a summary of verification status.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();

            if (IntegrityResult.IntegrityValid)
                parts.Add("Integrity: VALID");
            else
                parts.Add($"Integrity: FAILED ({IntegrityResult.ErrorMessage})");

            if (BlockchainResult != null)
            {
                if (BlockchainResult.Success)
                    parts.Add($"Blockchain: VALID (Block {BlockchainResult.BlockNumber})");
                else
                    parts.Add($"Blockchain: FAILED ({BlockchainResult.ErrorMessage})");
            }

            if (SealStatus?.IsSealed == true)
                parts.Add($"Seal: SEALED (since {SealStatus.SealedAt:O})");

            return string.Join("; ", parts);
        }
    }
}

/// <summary>
/// Information about the seal status of a block.
/// </summary>
public class SealStatusInfo
{
    /// <summary>Whether the block is sealed.</summary>
    public required bool IsSealed { get; init; }

    /// <summary>When the block was sealed.</summary>
    public DateTime? SealedAt { get; init; }

    /// <summary>Reason for sealing.</summary>
    public string? Reason { get; init; }

    /// <summary>Principal who applied the seal.</summary>
    public string? SealedBy { get; init; }

    /// <summary>Cryptographic seal token.</summary>
    public string? SealToken { get; init; }

    /// <summary>Creates a not-sealed status.</summary>
    public static SealStatusInfo NotSealed() => new() { IsSealed = false };

    /// <summary>Creates a sealed status.</summary>
    public static SealStatusInfo Sealed(DateTime sealedAt, string reason, string sealedBy, string sealToken) => new()
    {
        IsSealed = true,
        SealedAt = sealedAt,
        Reason = reason,
        SealedBy = sealedBy,
        SealToken = sealToken
    };
}

/// <summary>
/// Result of loading a single shard from storage.
/// </summary>
public class ShardLoadResult
{
    /// <summary>Index of the shard.</summary>
    public required int ShardIndex { get; init; }

    /// <summary>Shard data if successfully loaded.</summary>
    public byte[]? Data { get; init; }

    /// <summary>Whether the shard was loaded successfully from storage.</summary>
    public required bool LoadedSuccessfully { get; init; }

    /// <summary>Whether the shard passed integrity verification.</summary>
    public required bool IntegrityValid { get; init; }

    /// <summary>Expected hash from manifest.</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>Actual computed hash.</summary>
    public string? ActualHash { get; init; }

    /// <summary>Error message if loading failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of shard reconstruction operation with verification details.
/// </summary>
public class ShardReconstructionResult
{
    /// <summary>Whether reconstruction succeeded.</summary>
    public required bool Success { get; init; }

    /// <summary>Reconstructed data.</summary>
    public byte[]? Data { get; init; }

    /// <summary>Verification results for each shard.</summary>
    public required IReadOnlyList<ShardVerificationResult> ShardResults { get; init; }

    /// <summary>Indices of corrupted shards that were detected.</summary>
    public required IReadOnlyList<int> CorruptedShards { get; init; }

    /// <summary>Indices of missing shards.</summary>
    public required IReadOnlyList<int> MissingShards { get; init; }

    /// <summary>Indices of shards that were reconstructed using parity.</summary>
    public required IReadOnlyList<int> ReconstructedShards { get; init; }

    /// <summary>Error message if reconstruction failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Whether any shards needed reconstruction.</summary>
    public bool ReconstructionPerformed => ReconstructedShards.Count > 0;

    /// <summary>Total number of problematic shards.</summary>
    public int ProblematicShardCount => CorruptedShards.Count + MissingShards.Count;
}

/// <summary>
/// Version index for quick latest version lookup.
/// </summary>
public class VersionIndex
{
    /// <summary>Object ID this index is for.</summary>
    public required Guid ObjectId { get; init; }

    /// <summary>Latest version number.</summary>
    public required int LatestVersion { get; init; }

    /// <summary>When the index was last updated.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>All available versions.</summary>
    public List<int>? AvailableVersions { get; init; }
}

/// <summary>
/// Extension methods for audit trail integration with read operations.
/// </summary>
public static class ReadAuditTrailExtensions
{
    /// <summary>
    /// Logs a read operation to the audit trail.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID being read.</param>
    /// <param name="version">Version being read.</param>
    /// <param name="userId">User performing the read.</param>
    /// <param name="readMode">Read verification mode used.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogReadAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string? userId,
        ReadMode readMode,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["ReadMode"] = readMode.ToString()
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.Read,
            UserId: userId,
            Details: $"Read version {version} with {readMode} verification mode",
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs an integrity verification to the audit trail.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID being verified.</param>
    /// <param name="version">Version being verified.</param>
    /// <param name="isValid">Whether verification passed.</param>
    /// <param name="dataHash">Hash of the data (expected or actual).</param>
    /// <param name="userId">User who initiated the verification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogVerificationAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        bool isValid,
        string dataHash,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["VerificationResult"] = isValid ? "VALID" : "FAILED"
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.Verified,
            UserId: userId,
            Details: isValid
                ? $"Integrity verification passed for version {version}"
                : $"Integrity verification FAILED for version {version}",
            DataHash: dataHash,
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that corruption was detected.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID where corruption was detected.</param>
    /// <param name="version">Version affected.</param>
    /// <param name="expectedHash">Expected hash.</param>
    /// <param name="actualHash">Actual computed hash.</param>
    /// <param name="affectedShards">List of affected shard indices.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogCorruptionDetectedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string expectedHash,
        string actualHash,
        IReadOnlyList<int>? affectedShards,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["ExpectedHash"] = expectedHash,
            ["ActualHash"] = actualHash
        };

        if (affectedShards != null && affectedShards.Count > 0)
        {
            metadata["AffectedShards"] = string.Join(",", affectedShards);
        }

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.CorruptionDetected,
            UserId: null, // System detection
            Details: $"Corruption detected in version {version}: hash mismatch" +
                    (affectedShards != null && affectedShards.Count > 0
                        ? $", {affectedShards.Count} shards affected"
                        : ""),
            DataHash: actualHash,
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a recovery was attempted.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID being recovered.</param>
    /// <param name="version">Version being recovered.</param>
    /// <param name="recoverySource">Source of recovery (WORM, RAID, etc.).</param>
    /// <param name="userId">User who initiated recovery (null for automatic).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogRecoveryAttemptedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string recoverySource,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["RecoverySource"] = recoverySource
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.RecoveryAttempted,
            UserId: userId,
            Details: $"Recovery attempted for version {version} from {recoverySource}",
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a recovery succeeded.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID that was recovered.</param>
    /// <param name="version">Version that was recovered.</param>
    /// <param name="recoverySource">Source of recovery.</param>
    /// <param name="restoredHash">Hash of the restored data.</param>
    /// <param name="shardsRestored">Number of shards restored.</param>
    /// <param name="userId">User who initiated recovery.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogRecoverySucceededAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string recoverySource,
        string restoredHash,
        int shardsRestored,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["RecoverySource"] = recoverySource,
            ["ShardsRestored"] = shardsRestored.ToString()
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.RecoverySucceeded,
            UserId: userId,
            Details: $"Recovery succeeded for version {version} from {recoverySource}, {shardsRestored} shards restored",
            DataHash: restoredHash,
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a recovery failed.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID where recovery failed.</param>
    /// <param name="version">Version where recovery failed.</param>
    /// <param name="recoverySource">Source that was attempted.</param>
    /// <param name="errorMessage">Error message explaining the failure.</param>
    /// <param name="userId">User who initiated recovery.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogRecoveryFailedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string recoverySource,
        string errorMessage,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["RecoverySource"] = recoverySource,
            ["ErrorMessage"] = errorMessage
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.RecoveryFailed,
            UserId: userId,
            Details: $"Recovery failed for version {version} from {recoverySource}: {errorMessage}",
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a legal hold was applied.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID where legal hold was applied.</param>
    /// <param name="holdId">Legal hold identifier.</param>
    /// <param name="reason">Reason for the legal hold.</param>
    /// <param name="userId">User who applied the hold.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogLegalHoldAppliedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        string holdId,
        string reason,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["HoldId"] = holdId,
            ["HoldReason"] = reason
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.LegalHoldApplied,
            UserId: userId,
            Details: $"Legal hold applied: {reason}",
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a legal hold was released.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID where legal hold was released.</param>
    /// <param name="holdId">Legal hold identifier.</param>
    /// <param name="reason">Reason for releasing the hold.</param>
    /// <param name="userId">User who released the hold.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogLegalHoldReleasedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        string holdId,
        string? reason,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["HoldId"] = holdId
        };

        if (!string.IsNullOrEmpty(reason))
        {
            metadata["ReleaseReason"] = reason;
        }

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.LegalHoldReleased,
            UserId: userId,
            Details: $"Legal hold released: {holdId}" + (string.IsNullOrEmpty(reason) ? "" : $". Reason: {reason}"),
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a retention policy was applied.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID where policy was applied.</param>
    /// <param name="retentionDays">Number of days for retention.</param>
    /// <param name="expiryDate">Expiry date of the retention.</param>
    /// <param name="userId">User who applied the policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogRetentionPolicyAppliedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int retentionDays,
        DateTime expiryDate,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["RetentionDays"] = retentionDays.ToString(),
            ["ExpiryDate"] = expiryDate.ToString("O")
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.RetentionPolicyApplied,
            UserId: userId,
            Details: $"Retention policy applied: {retentionDays} days (expires {expiryDate:yyyy-MM-dd})",
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that an object was deleted.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID that was deleted.</param>
    /// <param name="version">Version deleted (null if all versions).</param>
    /// <param name="userId">User who deleted the object.</param>
    /// <param name="reason">Reason for deletion.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogDeletedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int? version,
        string? userId,
        string? reason,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>();

        if (!string.IsNullOrEmpty(reason))
        {
            metadata["DeletionReason"] = reason;
        }

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.Deleted,
            UserId: userId,
            Details: version.HasValue
                ? $"Deleted version {version.Value}"
                : "Deleted all versions",
            Version: version,
            Metadata: metadata.Count > 0 ? metadata : null);

        return await auditTrail.LogOperationAsync(operation, ct);
    }
}
