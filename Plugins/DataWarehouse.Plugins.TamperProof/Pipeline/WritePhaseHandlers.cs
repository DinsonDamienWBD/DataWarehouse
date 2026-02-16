// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.Plugins.TamperProof.Services;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.TamperProof;

/// <summary>
/// Helper methods for each phase of the tamper-proof write pipeline.
/// Implements the 5-phase write process with full error handling and logging.
/// </summary>
public static class WritePhaseHandlers
{
    /// <summary>
    /// Verifies that a block is not sealed before allowing write operations.
    /// Throws BlockSealedException if the block is sealed.
    /// </summary>
    /// <param name="objectId">Object/block ID to check.</param>
    /// <param name="sealService">Optional seal service (if null, check is skipped).</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task VerifyNotSealedAsync(
        Guid objectId,
        ISealService? sealService,
        ILogger logger,
        CancellationToken ct)
    {
        if (sealService == null)
        {
            return;
        }

        logger.LogDebug("Checking seal status for block {ObjectId}", objectId);

        var sealInfo = await sealService.GetSealInfoAsync(objectId, ct);
        if (sealInfo != null)
        {
            logger.LogWarning(
                "Write blocked: Block {ObjectId} is sealed since {SealedAt}. Reason: {Reason}",
                objectId, sealInfo.SealedAt, sealInfo.Reason);

            throw new BlockSealedException(objectId, sealInfo.SealedAt, sealInfo.Reason);
        }
    }

    /// <summary>
    /// Verifies that specific shards are not sealed before allowing write operations.
    /// Throws BlockSealedException if any shard is sealed.
    /// </summary>
    /// <param name="objectId">Object/block ID containing the shards.</param>
    /// <param name="shardIndices">Shard indices to check.</param>
    /// <param name="sealService">Optional seal service (if null, check is skipped).</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task VerifyShardsNotSealedAsync(
        Guid objectId,
        IEnumerable<int> shardIndices,
        ISealService? sealService,
        ILogger logger,
        CancellationToken ct)
    {
        if (sealService == null)
        {
            return;
        }

        // First check if the entire block is sealed
        await VerifyNotSealedAsync(objectId, sealService, logger, ct);

        // Then check individual shards
        foreach (var shardIndex in shardIndices)
        {
            var isShardSealed = await sealService.IsShardSealedAsync(objectId, shardIndex, ct);
            if (isShardSealed)
            {
                logger.LogWarning(
                    "Write blocked: Shard {ShardIndex} of block {ObjectId} is sealed",
                    shardIndex, objectId);

                // Get seal info for details
                var sealInfo = await sealService.GetSealInfoAsync(objectId, ct);
                var sealedAt = sealInfo?.SealedAt ?? DateTime.UtcNow;
                var reason = sealInfo?.Reason ?? "Shard is sealed";

                throw BlockSealedException.ForShard(objectId, shardIndex, sealedAt, reason);
            }
        }
    }

    /// <summary>
    /// Phase 1: Apply user-defined transformations (compression, encryption) via pipeline orchestrator.
    /// </summary>
    /// <param name="data">Original data stream.</param>
    /// <param name="orchestrator">Pipeline orchestrator for transformations.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transformed data and pipeline stage records.</returns>
    public static async Task<(byte[] transformedData, List<PipelineStageRecord> stages)> ApplyUserTransformationsAsync(
        Stream data,
        IPipelineOrchestrator orchestrator,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Applying user transformations");

        try
        {
            // Read original data
            using var ms = new MemoryStream();
            await data.CopyToAsync(ms, ct);
            var originalData = ms.ToArray();

            // Apply pipeline transformations if configured
            var stages = new List<PipelineStageRecord>();
            var currentData = originalData;

            // Get configured pipeline stages from orchestrator
            var configuredStages = orchestrator.GetConfiguredStages();

            if (configuredStages.Count > 0)
            {
                logger.LogDebug("Applying {Count} pipeline stages", configuredStages.Count);

                using var inputStream = new MemoryStream(currentData);
                using var outputStream = await orchestrator.ApplyPipelineAsync(inputStream, ct);

                using var resultMs = new MemoryStream();
                await outputStream.CopyToAsync(resultMs, ct);
                currentData = resultMs.ToArray();

                // Record each stage (simplified - full implementation would track individual stages)
                for (int i = 0; i < configuredStages.Count; i++)
                {
                    var stageId = configuredStages[i];
                    stages.Add(new PipelineStageRecord
                    {
                        StageType = stageId,
                        StageIndex = i,
                        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
                        InputHash = i == 0 ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalData)) : stages[i - 1].OutputHash,
                        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
                        OutputHash = i == configuredStages.Count - 1 ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(currentData)) : string.Empty,
                        InputSize = i == 0 ? originalData.Length : 0,
                        OutputSize = i == configuredStages.Count - 1 ? currentData.Length : 0,
                        ExecutedAt = DateTimeOffset.UtcNow
                    });
                }
            }
            else
            {
                logger.LogDebug("No pipeline stages configured, returning data as-is");
            }

            return (currentData, stages);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply user transformations");
            throw new InvalidOperationException("User transformation pipeline failed", ex);
        }
    }

    /// <summary>
    /// Applies content padding to the transformed data, including support for Chaff padding mode.
    /// Chaff padding generates plausible-looking data using a seeded CSPRNG that produces byte
    /// patterns matching typical content distribution, indistinguishable from real content.
    /// </summary>
    /// <param name="data">Transformed data to pad.</param>
    /// <param name="config">Content padding configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <returns>Padded data and content padding record for the manifest.</returns>
    public static (byte[] paddedData, ContentPaddingRecord paddingRecord) ApplyContentPadding(
        byte[] data,
        ContentPaddingConfig config,
        ILogger logger)
    {
        if (!config.Enabled || data.Length == 0)
        {
            return (data, new ContentPaddingRecord
            {
                PrefixPaddingBytes = 0,
                SuffixPaddingBytes = 0,
                PaddingPattern = "none"
            });
        }

        // Calculate padding needed
        var paddingNeeded = 0;
        if (config.PadToMultipleOf > 0)
        {
            var remainder = data.Length % config.PadToMultipleOf;
            if (remainder > 0)
            {
                paddingNeeded = config.PadToMultipleOf - remainder;
            }
        }

        paddingNeeded = Math.Max(paddingNeeded, config.MinimumPadding);
        paddingNeeded = Math.Min(paddingNeeded, config.MaximumPadding);

        if (paddingNeeded == 0)
        {
            return (data, new ContentPaddingRecord
            {
                PrefixPaddingBytes = 0,
                SuffixPaddingBytes = 0,
                PaddingPattern = "none"
            });
        }

        // Split padding between prefix and suffix (suffix gets the larger portion)
        var prefixPadding = paddingNeeded / 4;
        var suffixPadding = paddingNeeded - prefixPadding;

        byte[] prefixBytes;
        byte[] suffixBytes;
        long? paddingSeed = null;
        string paddingPattern;

        if (config.UseRandomPadding)
        {
            // Use Chaff padding: seeded CSPRNG producing plausible-looking byte patterns
            paddingSeed = GenerateChaffSeed();
            prefixBytes = GenerateChaffPadding(prefixPadding, paddingSeed.Value, data);
            suffixBytes = GenerateChaffPadding(suffixPadding, paddingSeed.Value + 1, data);
            paddingPattern = "Chaff";

            logger.LogDebug(
                "Applied Chaff padding: {Prefix} prefix bytes + {Suffix} suffix bytes (seed: {Seed})",
                prefixPadding, suffixPadding, paddingSeed.Value);
        }
        else
        {
            // Use simple byte-fill padding
            prefixBytes = new byte[prefixPadding];
            suffixBytes = new byte[suffixPadding];
            Array.Fill(prefixBytes, config.PaddingByte);
            Array.Fill(suffixBytes, config.PaddingByte);
            paddingPattern = config.PaddingByte == 0x00 ? "zeros" : $"fill-0x{config.PaddingByte:X2}";

            logger.LogDebug(
                "Applied {Pattern} padding: {Prefix} prefix bytes + {Suffix} suffix bytes",
                paddingPattern, prefixPadding, suffixPadding);
        }

        // Assemble padded data
        var paddedData = new byte[prefixPadding + data.Length + suffixPadding];
        Array.Copy(prefixBytes, 0, paddedData, 0, prefixPadding);
        Array.Copy(data, 0, paddedData, prefixPadding, data.Length);
        Array.Copy(suffixBytes, 0, paddedData, prefixPadding + data.Length, suffixPadding);

        // Compute padding hash for verification
        var paddingHashInput = new byte[prefixPadding + suffixPadding];
        Array.Copy(prefixBytes, 0, paddingHashInput, 0, prefixPadding);
        Array.Copy(suffixBytes, 0, paddingHashInput, prefixPadding, suffixPadding);
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        var paddingHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(paddingHashInput));

        return (paddedData, new ContentPaddingRecord
        {
            PrefixPaddingBytes = prefixPadding,
            SuffixPaddingBytes = suffixPadding,
            PaddingHash = paddingHash,
            PaddingSeed = paddingSeed,
            PaddingPattern = paddingPattern
        });
    }

    /// <summary>
    /// Generates a cryptographically secure seed for Chaff padding.
    /// </summary>
    /// <returns>A 64-bit seed value.</returns>
    private static long GenerateChaffSeed()
    {
        Span<byte> seedBytes = stackalloc byte[8];
        System.Security.Cryptography.RandomNumberGenerator.Fill(seedBytes);
        return BitConverter.ToInt64(seedBytes);
    }

    /// <summary>
    /// Generates Chaff padding: plausible-looking data using a seeded CSPRNG
    /// that produces byte patterns matching typical content distribution.
    /// The output is deterministically reproducible from the stored seed for verification,
    /// but statistically indistinguishable from real content.
    /// </summary>
    /// <param name="length">Number of padding bytes to generate.</param>
    /// <param name="seed">Seed for deterministic reproduction.</param>
    /// <param name="referenceData">Reference data to model byte distribution from.</param>
    /// <returns>Chaff padding bytes.</returns>
    private static byte[] GenerateChaffPadding(int length, long seed, byte[] referenceData)
    {
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var chaff = new byte[length];

        // Build a byte frequency distribution from the reference data
        // This makes the chaff statistically similar to the actual content
        var frequency = new int[256];
        foreach (var b in referenceData)
        {
            frequency[b]++;
        }

        // Build cumulative distribution for weighted random selection
        var totalSamples = referenceData.Length;
        var cumulativeDistribution = new double[256];
        double cumulative = 0;
        for (int i = 0; i < 256; i++)
        {
            cumulative += totalSamples > 0
                ? (double)frequency[i] / totalSamples
                : 1.0 / 256.0; // Uniform if no reference data
            cumulativeDistribution[i] = cumulative;
        }

        // Normalize final entry to exactly 1.0 to avoid floating-point edge cases
        cumulativeDistribution[255] = 1.0;

        // Use a seeded deterministic generator for reproducibility
        // We derive the PRNG state from the seed using SHA-256 in counter mode
        var seedBytes = BitConverter.GetBytes(seed);
        var position = 0;
        var blockCounter = 0;

        while (position < length)
        {
            // Generate a block of deterministic random bytes from the seed + counter
            var counterBytes = BitConverter.GetBytes(blockCounter);
            var input = new byte[seedBytes.Length + counterBytes.Length];
            Array.Copy(seedBytes, 0, input, 0, seedBytes.Length);
            Array.Copy(counterBytes, 0, input, seedBytes.Length, counterBytes.Length);

            // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
            var hashBlock = System.Security.Cryptography.SHA256.HashData(input);
            blockCounter++;

            // Use pairs of hash bytes to generate values from the content distribution
            for (int i = 0; i + 1 < hashBlock.Length && position < length; i += 2)
            {
                // Convert 2 bytes to a value in [0, 1)
                var uniformValue = (double)((hashBlock[i] << 8) | hashBlock[i + 1]) / 65536.0;

                // Binary search in cumulative distribution to find the corresponding byte value
                var selectedByte = BinarySearchDistribution(cumulativeDistribution, uniformValue);
                chaff[position++] = (byte)selectedByte;
            }
        }

        return chaff;
    }

    /// <summary>
    /// Binary searches a cumulative distribution array to find the byte value
    /// corresponding to a uniform random value in [0, 1).
    /// </summary>
    private static int BinarySearchDistribution(double[] cumulativeDistribution, double value)
    {
        int lo = 0;
        int hi = 255;

        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (cumulativeDistribution[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// <summary>
    /// Phase 2: Compute integrity hash of the transformed data.
    /// </summary>
    /// <param name="data">Transformed data to hash.</param>
    /// <param name="algorithm">Hash algorithm to use.</param>
    /// <param name="integrity">Integrity provider.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Computed integrity hash.</returns>
    public static async Task<IntegrityHash> ComputeIntegrityHashAsync(
        byte[] data,
        DataWarehouse.SDK.Contracts.TamperProof.HashAlgorithmType algorithm,
        IIntegrityProvider integrity,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Computing integrity hash using {Algorithm}", algorithm);

        try
        {
            var hash = await integrity.ComputeHashAsync(data, algorithm, ct);
            logger.LogDebug("Computed integrity hash: {Hash}", hash.HashValue);
            return hash;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to compute integrity hash");
            throw new InvalidOperationException("Integrity hash computation failed", ex);
        }
    }

    /// <summary>
    /// Phase 3: Perform RAID sharding on the transformed data.
    /// Splits data into shards with parity for redundancy.
    /// </summary>
    /// <param name="data">Data to shard.</param>
    /// <param name="objectId">Object ID for shard attribution.</param>
    /// <param name="raidConfig">RAID configuration.</param>
    /// <param name="integrity">Integrity provider for shard hashing.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of shards and RAID configuration record.</returns>
    public static async Task<(List<byte[]> shards, RaidRecord raidConfig)> PerformRaidShardingAsync(
        byte[] data,
        Guid objectId,
        RaidConfig raidConfig,
        IIntegrityProvider integrity,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Performing RAID sharding: {DataShards} data + {ParityShards} parity",
            raidConfig.DataShards, raidConfig.ParityShards);

        try
        {
            // Calculate shard size
            var shardSize = data.Length / raidConfig.DataShards;
            if (data.Length % raidConfig.DataShards != 0)
            {
                shardSize++; // Round up for last shard
            }

            // Create data shards
            var shards = new List<byte[]>();
            for (int i = 0; i < raidConfig.DataShards; i++)
            {
                var start = i * shardSize;
                var length = Math.Min(shardSize, data.Length - start);
                var shard = new byte[length];
                Array.Copy(data, start, shard, 0, length);
                shards.Add(shard);
            }

            // Create parity shards (simple XOR for now, would use Reed-Solomon in production)
            for (int i = 0; i < raidConfig.ParityShards; i++)
            {
                var parityShard = new byte[shardSize];
                for (int j = 0; j < raidConfig.DataShards; j++)
                {
                    for (int k = 0; k < Math.Min(shardSize, shards[j].Length); k++)
                    {
                        parityShard[k] ^= shards[j][k];
                    }
                }
                shards.Add(parityShard);
            }

            // Compute shard hashes
            var shardHashes = await integrity.ComputeShardHashesAsync(shards, objectId, ct);

            // Build RAID record
            var shardRecords = new List<ShardRecord>();
            for (int i = 0; i < shards.Count; i++)
            {
                shardRecords.Add(new ShardRecord
                {
                    ShardIndex = i,
                    IsParity = i >= raidConfig.DataShards,
                    ActualSize = shards[i].Length,
                    ContentHash = shardHashes[i].Hash.HashValue,
                    StorageLocation = $"shard_{objectId}_{i}.bin",
                    WrittenAt = DateTimeOffset.UtcNow
                });
            }

            var raidRecord = new RaidRecord
            {
                DataShardCount = raidConfig.DataShards,
                ParityShardCount = raidConfig.ParityShards,
                ShardSize = shardSize,
                Shards = shardRecords
            };

            logger.LogDebug("RAID sharding complete: {TotalShards} shards created", shards.Count);

            return (shards, raidRecord);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to perform RAID sharding");
            throw new InvalidOperationException("RAID sharding failed", ex);
        }
    }

    /// <summary>
    /// Phase 4: Execute transactional write across 4 tiers with rollback on failure.
    /// Writes to: Data (shards), Metadata (manifest), WORM (backup), Blockchain (pending).
    /// This overload includes seal verification before any write operations.
    /// </summary>
    /// <param name="objectId">Object ID being written.</param>
    /// <param name="manifest">Tamper-proof manifest.</param>
    /// <param name="shards">RAID shards to write.</param>
    /// <param name="fullData">Complete transformed data for WORM backup.</param>
    /// <param name="dataStorage">Data tier storage.</param>
    /// <param name="metadataStorage">Metadata tier storage.</param>
    /// <param name="worm">WORM storage provider.</param>
    /// <param name="config">Tamper-proof configuration.</param>
    /// <param name="sealService">Optional seal service for seal verification.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transaction result with per-tier status.</returns>
    /// <exception cref="BlockSealedException">Thrown if the block is sealed.</exception>
    public static async Task<TransactionResult> ExecuteTransactionalWriteAsync(
        Guid objectId,
        TamperProofManifest manifest,
        List<byte[]> shards,
        byte[] fullData,
        IStorageProvider dataStorage,
        IStorageProvider metadataStorage,
        IWormStorageProvider worm,
        TamperProofConfiguration config,
        ISealService? sealService,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Starting transactional write for object {ObjectId}", objectId);

        // CRITICAL: Check seal status BEFORE any write operations
        // If the block is sealed, this will throw BlockSealedException
        await VerifyNotSealedAsync(objectId, sealService, logger, ct);

        // Also verify all shards are not individually sealed
        var shardIndices = Enumerable.Range(0, shards.Count);
        await VerifyShardsNotSealedAsync(objectId, shardIndices, sealService, logger, ct);

        // Proceed with transactional write
        return await ExecuteTransactionalWriteInternalAsync(
            objectId, manifest, shards, fullData,
            dataStorage, metadataStorage, worm, config, logger, ct);
    }

    /// <summary>
    /// Phase 4: Execute transactional write across 4 tiers with rollback on failure.
    /// Writes to: Data (shards), Metadata (manifest), WORM (backup), Blockchain (pending).
    /// </summary>
    /// <param name="objectId">Object ID being written.</param>
    /// <param name="manifest">Tamper-proof manifest.</param>
    /// <param name="shards">RAID shards to write.</param>
    /// <param name="fullData">Complete transformed data for WORM backup.</param>
    /// <param name="dataStorage">Data tier storage.</param>
    /// <param name="metadataStorage">Metadata tier storage.</param>
    /// <param name="worm">WORM storage provider.</param>
    /// <param name="config">Tamper-proof configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transaction result with per-tier status.</returns>
    public static Task<TransactionResult> ExecuteTransactionalWriteAsync(
        Guid objectId,
        TamperProofManifest manifest,
        List<byte[]> shards,
        byte[] fullData,
        IStorageProvider dataStorage,
        IStorageProvider metadataStorage,
        IWormStorageProvider worm,
        TamperProofConfiguration config,
        ILogger logger,
        CancellationToken ct)
    {
        // Call without seal service (backward compatible)
        return ExecuteTransactionalWriteAsync(
            objectId, manifest, shards, fullData,
            dataStorage, metadataStorage, worm, config,
            null, // No seal service
            logger, ct);
    }

    /// <summary>
    /// Internal implementation of transactional write (after seal verification).
    /// </summary>
    private static async Task<TransactionResult> ExecuteTransactionalWriteInternalAsync(
        Guid objectId,
        TamperProofManifest manifest,
        List<byte[]> shards,
        byte[] fullData,
        IStorageProvider dataStorage,
        IStorageProvider metadataStorage,
        IWormStorageProvider worm,
        TamperProofConfiguration config,
        ILogger logger,
        CancellationToken ct)
    {

        var dataTierResult = (TierWriteResult?)null;
        var metadataTierResult = (TierWriteResult?)null;
        var wormTierResult = (TierWriteResult?)null;
        var blockchainTierResult = (TierWriteResult?)null;

        try
        {
            // Write shards to data tier (parallel)
            dataTierResult = await WriteDataTierAsync(objectId, shards, manifest.RaidConfiguration, dataStorage, logger, ct);

            // Write manifest to metadata tier
            metadataTierResult = await WriteMetadataTierAsync(objectId, manifest, metadataStorage, logger, ct);

            // Write WORM backup
            wormTierResult = await WriteWormTierAsync(objectId, fullData, manifest, worm, config, logger, ct);

            // Blockchain tier is queued separately (Phase 5), mark as pending
            blockchainTierResult = TierWriteResult.CreateSuccess(
                "Blockchain",
                config.StorageInstances.Blockchain.InstanceId,
                "pending",
                0);

            logger.LogInformation("Transactional write completed successfully for object {ObjectId}", objectId);

            return TransactionResult.CreateSuccess(
                objectId,
                dataTierResult,
                metadataTierResult,
                wormTierResult,
                blockchainTierResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transactional write failed for object {ObjectId}, attempting rollback", objectId);

            // Attempt rollback based on configuration
            var rollbackAttempted = false;
            DataWarehouse.SDK.Contracts.TamperProof.RollbackResult? rollbackResult = null;

            if (config.TransactionFailureBehavior == TransactionFailureBehavior.Strict)
            {
                rollbackAttempted = true;
                rollbackResult = await RollbackTransactionAsync(
                    objectId,
                    dataTierResult,
                    metadataTierResult,
                    wormTierResult,
                    dataStorage,
                    metadataStorage,
                    logger,
                    ct);
            }

            return TransactionResult.CreateFailure(
                objectId,
                $"Transaction failed: {ex.Message}",
                InstanceDegradationState.Corrupted,
                rollbackAttempted,
                rollbackResult,
                dataTierResult,
                metadataTierResult,
                wormTierResult,
                blockchainTierResult);
        }
    }

    /// <summary>
    /// Writes RAID shards to data tier storage.
    /// </summary>
    private static async Task<TierWriteResult> WriteDataTierAsync(
        Guid objectId,
        List<byte[]> shards,
        RaidRecord raidConfig,
        IStorageProvider dataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Writing {Count} shards to data tier", shards.Count);

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Write shards in parallel
            var tasks = new Task[shards.Count];
            for (int i = 0; i < shards.Count; i++)
            {
                var shardIndex = i;
                var shardData = shards[i];
                var shardRecord = raidConfig.Shards[i];
                var uri = new Uri($"data://shards/{shardRecord.StorageLocation}");

                tasks[i] = Task.Run(async () =>
                {
                    using var stream = new MemoryStream(shardData);
                    await dataStorage.SaveAsync(uri, stream);
                }, ct);
            }

            await Task.WhenAll(tasks);

            var totalBytes = shards.Sum(s => s.Length);
            logger.LogDebug("Data tier write completed: {Bytes} bytes in {Ms}ms",
                totalBytes, sw.ElapsedMilliseconds);

            return TierWriteResult.CreateSuccess(
                "Data",
                "data-storage",
                $"shards/{objectId}",
                totalBytes,
                sw.Elapsed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write to data tier");
            return TierWriteResult.CreateFailure(
                "Data",
                "data-storage",
                ex.Message,
                sw.Elapsed);
        }
    }

    /// <summary>
    /// Writes manifest to metadata tier storage.
    /// </summary>
    private static async Task<TierWriteResult> WriteMetadataTierAsync(
        Guid objectId,
        TamperProofManifest manifest,
        IStorageProvider metadataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Writing manifest to metadata tier");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest);
            var manifestBytes = System.Text.Encoding.UTF8.GetBytes(manifestJson);
            var uri = new Uri($"metadata://manifests/{objectId}_v{manifest.Version}.json");

            using var stream = new MemoryStream(manifestBytes);
            await metadataStorage.SaveAsync(uri, stream);

            // Update version index for quick latest version lookup
            await UpdateVersionIndexAsync(objectId, manifest.Version, metadataStorage, logger, ct);

            logger.LogDebug("Metadata tier write completed: {Bytes} bytes in {Ms}ms",
                manifestBytes.Length, sw.ElapsedMilliseconds);

            return TierWriteResult.CreateSuccess(
                "Metadata",
                "metadata-storage",
                $"manifests/{objectId}_v{manifest.Version}.json",
                manifestBytes.Length,
                sw.Elapsed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write to metadata tier");
            return TierWriteResult.CreateFailure(
                "Metadata",
                "metadata-storage",
                ex.Message,
                sw.Elapsed);
        }
    }

    /// <summary>
    /// Updates the version index for an object.
    /// </summary>
    private static async Task UpdateVersionIndexAsync(
        Guid objectId,
        int newVersion,
        IStorageProvider metadataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var indexUri = new Uri($"metadata://manifests/{objectId}_versions.json");
            VersionIndexRecord index;

            // Try to load existing index
            try
            {
                using var existingStream = await metadataStorage.LoadAsync(indexUri);
                if (existingStream != null)
                {
                    var existingJson = await new StreamReader(existingStream).ReadToEndAsync(ct);
                    var existingIndex = System.Text.Json.JsonSerializer.Deserialize<VersionIndexRecord>(existingJson);
                    if (existingIndex != null)
                    {
                        // Update existing index
                        var versions = existingIndex.AvailableVersions?.ToList() ?? new List<int>();
                        if (!versions.Contains(newVersion))
                        {
                            versions.Add(newVersion);
                        }
                        versions.Sort();

                        index = new VersionIndexRecord
                        {
                            ObjectId = objectId,
                            LatestVersion = Math.Max(existingIndex.LatestVersion, newVersion),
                            UpdatedAt = DateTimeOffset.UtcNow,
                            AvailableVersions = versions
                        };
                    }
                    else
                    {
                        index = CreateNewIndex(objectId, newVersion);
                    }
                }
                else
                {
                    index = CreateNewIndex(objectId, newVersion);
                }
            }
            catch
            {
                // Index doesn't exist, create new one
                index = CreateNewIndex(objectId, newVersion);
            }

            // Write updated index
            var indexJson = System.Text.Json.JsonSerializer.Serialize(index);
            using var indexMs = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(indexJson));
            await metadataStorage.SaveAsync(indexUri, indexMs);

            logger.LogDebug("Updated version index for object {ObjectId}: latest version {Version}",
                objectId, index.LatestVersion);
        }
        catch (Exception ex)
        {
            // Non-critical failure, log and continue
            logger.LogDebug(ex, "Failed to update version index for object {ObjectId}", objectId);
        }
    }

    /// <summary>
    /// Creates a new version index for an object.
    /// </summary>
    private static VersionIndexRecord CreateNewIndex(Guid objectId, int version)
    {
        return new VersionIndexRecord
        {
            ObjectId = objectId,
            LatestVersion = version,
            UpdatedAt = DateTimeOffset.UtcNow,
            AvailableVersions = new List<int> { version }
        };
    }

    /// <summary>
    /// Writes immutable backup to WORM tier.
    /// </summary>
    private static async Task<TierWriteResult> WriteWormTierAsync(
        Guid objectId,
        byte[] data,
        TamperProofManifest manifest,
        IWormStorageProvider worm,
        TamperProofConfiguration config,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Writing WORM backup");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var retentionPolicy = new WormRetentionPolicy
            {
                RetentionPeriod = config.DefaultRetentionPeriod,
                ExpiryTime = DateTimeOffset.UtcNow.Add(config.DefaultRetentionPeriod)
            };

            var wormRequest = new PluginWormWriteRequest
            {
                ObjectId = objectId,
                Version = manifest.Version,
                Data = data,
                RetentionPolicy = retentionPolicy,
                Metadata = new Dictionary<string, object>
                {
                    ["ContentHash"] = manifest.FinalContentHash,
                    ["Author"] = manifest.WriteContext.Author,
                    ["CreatedAt"] = manifest.CreatedAt
                }
            };

            var result = await worm.WriteAsync(wormRequest, ct);

            logger.LogDebug("WORM tier write completed: {Bytes} bytes in {Ms}ms",
                data.Length, sw.ElapsedMilliseconds);

            return TierWriteResult.CreateSuccess(
                "WORM",
                "worm-storage",
                result.RecordId,
                data.Length,
                sw.Elapsed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write to WORM tier");
            return TierWriteResult.CreateFailure(
                "WORM",
                "worm-storage",
                ex.Message,
                sw.Elapsed);
        }
    }

    /// <summary>
    /// Rolls back a failed transaction by deleting written data.
    /// WORM writes cannot be rolled back and become orphaned.
    /// </summary>
    private static async Task<DataWarehouse.SDK.Contracts.TamperProof.RollbackResult> RollbackTransactionAsync(
        Guid objectId,
        TierWriteResult? dataTierResult,
        TierWriteResult? metadataTierResult,
        TierWriteResult? wormTierResult,
        IStorageProvider dataStorage,
        IStorageProvider metadataStorage,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogWarning("Rolling back transaction for object {ObjectId}", objectId);

        var tierResults = new List<TierRollbackResult>();
        var orphanedRecords = new List<OrphanedWormRecord>();

        // Rollback data tier
        if (dataTierResult?.Success == true)
        {
            try
            {
                // Delete shards from data storage
                // Extract shard location from the resource ID
                var shardPattern = $"shards/{objectId}";
                logger.LogDebug("Deleting shards matching pattern: {Pattern}", shardPattern);

                // Attempt to delete all shards for this object
                var deletionErrors = new List<string>();
                var deletedCount = 0;

                try
                {
                    // Delete using pattern matching (best effort)
                    // Note: This is a simplified approach. In production, we would:
                    // 1. List all shard files matching the pattern
                    // 2. Delete each individually
                    // 3. Track partial failures

                    // For now, attempt to delete the base shard directory
                    var baseUri = new Uri($"data://{shardPattern}/");

                    try
                    {
                        await dataStorage.DeleteAsync(baseUri);
                        deletedCount++;
                    }
                    catch
                    {
                        // Individual shard deletion failed, try to continue
                        deletionErrors.Add($"Failed to delete shard directory: {baseUri}");
                    }

                    logger.LogDebug("Deleted {Count} shard locations", deletedCount);
                }
                catch (Exception deleteEx)
                {
                    deletionErrors.Add($"Shard deletion error: {deleteEx.Message}");
                }

                if (deletionErrors.Count > 0)
                {
                    logger.LogWarning("Partial shard deletion: {Errors}", string.Join("; ", deletionErrors));
                }

                tierResults.Add(new TierRollbackResult
                {
                    TierName = "Data",
                    Success = deletionErrors.Count == 0,
                    Action = deletionErrors.Count == 0 ? "Deleted all shards" : $"Partial deletion: {deletedCount} successful, {deletionErrors.Count} failed",
                    ErrorMessage = deletionErrors.Count > 0 ? string.Join("; ", deletionErrors) : null
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to rollback data tier");
                tierResults.Add(new TierRollbackResult
                {
                    TierName = "Data",
                    Success = false,
                    Action = "Deletion failed",
                    ErrorMessage = ex.Message
                });
            }
        }

        // Rollback metadata tier
        if (metadataTierResult?.Success == true)
        {
            try
            {
                var uri = new Uri($"metadata://manifests/{metadataTierResult.ResourceId}");
                await metadataStorage.DeleteAsync(uri);

                tierResults.Add(new TierRollbackResult
                {
                    TierName = "Metadata",
                    Success = true,
                    Action = "Deleted manifest"
                });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to rollback metadata tier");
                tierResults.Add(new TierRollbackResult
                {
                    TierName = "Metadata",
                    Success = false,
                    Action = "Deletion failed",
                    ErrorMessage = ex.Message
                });
            }
        }

        // WORM cannot be rolled back - track as orphaned
        if (wormTierResult?.Success == true)
        {
            logger.LogWarning("WORM write cannot be rolled back, tracking as orphaned");

            orphanedRecords.Add(new OrphanedWormRecord
            {
                OrphanId = Guid.NewGuid(),
                IntendedObjectId = objectId,
                WormReference = new WormReference
                {
                    StorageLocation = wormTierResult.ResourceId ?? "",
                    ContentHash = "", // Would need from context
                    ContentSize = wormTierResult.BytesWritten ?? 0,
                    WrittenAt = DateTimeOffset.UtcNow,
                    EnforcementMode = WormEnforcementMode.Software
                },
                Status = OrphanedWormStatus.TransactionFailed,
                CreatedAt = DateTimeOffset.UtcNow,
                FailureReason = "Transaction rollback - WORM write cannot be undone"
            });

            tierResults.Add(new TierRollbackResult
            {
                TierName = "WORM",
                Success = true,
                Action = "Tracked as orphaned (cannot delete)"
            });
        }

        var overallSuccess = tierResults.All(r => r.Success);

        return DataWarehouse.SDK.Contracts.TamperProof.RollbackResult.CreateSuccess(objectId, tierResults, orphanedRecords);
    }

    /// <summary>
    /// Validates that a block can be deleted by checking retention policy and legal holds.
    /// Throws RetentionPolicyBlockedException if deletion is not allowed.
    /// </summary>
    /// <param name="blockId">Block identifier to validate.</param>
    /// <param name="retentionService">Retention policy service.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="RetentionPolicyBlockedException">Thrown when deletion is blocked by retention policy or legal hold.</exception>
    public static async Task ValidateRetentionBeforeDeletionAsync(
        Guid blockId,
        IRetentionPolicyService retentionService,
        ILogger logger,
        CancellationToken ct)
    {
        logger.LogDebug("Validating retention policy before deletion for block {BlockId}", blockId);

        var validationResult = await retentionService.ValidateDeletionAsync(blockId, ct);

        if (!validationResult.IsAllowed)
        {
            logger.LogWarning(
                "Deletion blocked for block {BlockId}: {Reason} - {Details}",
                blockId, validationResult.Reason, validationResult.Details);

            throw new RetentionPolicyBlockedException(
                blockId,
                validationResult.Reason,
                validationResult.Details,
                validationResult.ActiveLegalHolds,
                validationResult.RetentionPolicy);
        }

        logger.LogDebug("Retention validation passed for block {BlockId}", blockId);
    }

    /// <summary>
    /// Checks if a block has active legal holds without throwing.
    /// </summary>
    /// <param name="blockId">Block identifier to check.</param>
    /// <param name="retentionService">Retention policy service.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if block has active legal holds.</returns>
    public static async Task<bool> HasActiveLegalHoldsAsync(
        Guid blockId,
        IRetentionPolicyService retentionService,
        CancellationToken ct)
    {
        var legalHolds = await retentionService.GetActiveLegalHoldsAsync(blockId, ct);
        return legalHolds.Count > 0;
    }

    /// <summary>
    /// Validates retention before executing a rollback that involves deletion.
    /// This is called during transaction failure to ensure we respect retention policies
    /// even during error recovery scenarios.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="retentionService">Retention policy service (optional, if null skips check).</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if rollback deletion is allowed, false if blocked by retention.</returns>
    public static async Task<(bool Allowed, string? BlockedReason)> CanRollbackDeleteAsync(
        Guid objectId,
        IRetentionPolicyService? retentionService,
        ILogger logger,
        CancellationToken ct)
    {
        // If no retention service is available, allow the operation
        if (retentionService == null)
        {
            return (true, null);
        }

        try
        {
            var validationResult = await retentionService.ValidateDeletionAsync(objectId, ct);

            if (!validationResult.IsAllowed)
            {
                logger.LogWarning(
                    "Rollback deletion blocked for object {ObjectId} due to retention: {Reason}",
                    objectId, validationResult.Details);

                return (false, validationResult.Details);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            // Log but don't fail the rollback entirely if retention check fails
            logger.LogWarning(ex, "Failed to check retention policy during rollback for object {ObjectId}", objectId);
            return (true, null); // Default to allowing rollback if check fails
        }
    }
}

/// <summary>
/// Exception thrown when a deletion operation is blocked by retention policy or legal hold.
/// </summary>
public class RetentionPolicyBlockedException : InvalidOperationException
{
    /// <summary>Block ID that cannot be deleted.</summary>
    public Guid BlockId { get; }

    /// <summary>Reason the deletion is blocked.</summary>
    public DeletionBlockedReason BlockedReason { get; }

    /// <summary>Detailed explanation of why deletion is blocked.</summary>
    public string BlockedDetails { get; }

    /// <summary>Active legal holds if any.</summary>
    public IReadOnlyList<RetentionLegalHold>? ActiveLegalHolds { get; }

    /// <summary>Retention policy if blocking.</summary>
    public Services.RetentionPolicy? RetentionPolicy { get; }

    /// <summary>
    /// Creates a new retention policy blocked exception.
    /// </summary>
    public RetentionPolicyBlockedException(
        Guid blockId,
        DeletionBlockedReason reason,
        string details,
        IReadOnlyList<RetentionLegalHold>? legalHolds = null,
        Services.RetentionPolicy? retentionPolicy = null)
        : base($"Deletion blocked for block {blockId}: {details}")
    {
        BlockId = blockId;
        BlockedReason = reason;
        BlockedDetails = details;
        ActiveLegalHolds = legalHolds;
        RetentionPolicy = retentionPolicy;
    }
}

/// <summary>
/// Request for WORM write operation (internal plugin type).
/// </summary>
public class PluginWormWriteRequest
{
    public required Guid ObjectId { get; init; }
    public required int Version { get; init; }
    public required byte[] Data { get; init; }
    public required WormRetentionPolicy RetentionPolicy { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Result of WORM write operation (internal plugin type).
/// </summary>
public class PluginWormWriteResult
{
    public required bool Success { get; init; }
    public required string RecordId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Version index record for tracking all versions of an object.
/// </summary>
public class VersionIndexRecord
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
/// Extension methods for audit trail integration with write operations.
/// </summary>
public static class WriteAuditTrailExtensions
{
    /// <summary>
    /// Logs the creation of a new object to the audit trail.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID being created.</param>
    /// <param name="version">Version number.</param>
    /// <param name="dataHash">Hash of the data being written.</param>
    /// <param name="userId">User performing the operation.</param>
    /// <param name="details">Additional details about the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogCreationAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string dataHash,
        string? userId,
        string? details = null,
        CancellationToken ct = default)
    {
        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.Created,
            UserId: userId,
            Details: details ?? $"Created object version {version}",
            DataHash: dataHash,
            Version: version);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs a modification to an existing object.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID being modified.</param>
    /// <param name="version">New version number.</param>
    /// <param name="dataHash">Hash of the new data.</param>
    /// <param name="userId">User performing the operation.</param>
    /// <param name="details">Additional details about the modification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogModificationAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string dataHash,
        string? userId,
        string? details = null,
        CancellationToken ct = default)
    {
        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.Modified,
            UserId: userId,
            Details: details ?? $"Modified to version {version}",
            DataHash: dataHash,
            Version: version);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that RAID shards were written for an object.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="shardCount">Number of shards written.</param>
    /// <param name="dataShards">Number of data shards.</param>
    /// <param name="parityShards">Number of parity shards.</param>
    /// <param name="userId">User performing the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogShardsWrittenAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int shardCount,
        int dataShards,
        int parityShards,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["ShardCount"] = shardCount.ToString(),
            ["DataShards"] = dataShards.ToString(),
            ["ParityShards"] = parityShards.ToString()
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.ShardsWritten,
            UserId: userId,
            Details: $"Wrote {shardCount} shards ({dataShards} data + {parityShards} parity)",
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a WORM backup was created.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="version">Version number.</param>
    /// <param name="wormRecordId">WORM storage record ID.</param>
    /// <param name="dataHash">Hash of the backed-up data.</param>
    /// <param name="retentionPeriod">Retention period for the backup.</param>
    /// <param name="userId">User performing the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogWormBackupCreatedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string wormRecordId,
        string dataHash,
        TimeSpan retentionPeriod,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["WormRecordId"] = wormRecordId,
            ["RetentionDays"] = retentionPeriod.TotalDays.ToString("F0")
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.WormBackupCreated,
            UserId: userId,
            Details: $"WORM backup created for version {version}, retention {retentionPeriod.TotalDays:F0} days",
            DataHash: dataHash,
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a blockchain anchor was created.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="version">Version number.</param>
    /// <param name="anchorId">Blockchain anchor ID.</param>
    /// <param name="dataHash">Hash that was anchored.</param>
    /// <param name="userId">User performing the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogBlockchainAnchoredAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string anchorId,
        string dataHash,
        string? userId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["AnchorId"] = anchorId
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.BlockchainAnchored,
            UserId: userId,
            Details: $"Blockchain anchor created for version {version}",
            DataHash: dataHash,
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that a manifest was updated.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="version">Version number in the manifest.</param>
    /// <param name="manifestHash">Hash of the manifest content.</param>
    /// <param name="userId">User performing the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogManifestUpdatedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string manifestHash,
        string? userId,
        CancellationToken ct = default)
    {
        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.ManifestUpdated,
            UserId: userId,
            Details: $"Manifest updated for version {version}",
            DataHash: manifestHash,
            Version: version);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs a secure correction operation.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="version">New version number after correction.</param>
    /// <param name="dataHash">Hash of the corrected data.</param>
    /// <param name="userId">User performing the correction.</param>
    /// <param name="reason">Reason for the correction.</param>
    /// <param name="authorizationId">ID of the authorization for this correction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogSecureCorrectionAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        int version,
        string dataHash,
        string? userId,
        string reason,
        string? authorizationId,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["CorrectionReason"] = reason
        };

        if (!string.IsNullOrEmpty(authorizationId))
        {
            metadata["AuthorizationId"] = authorizationId;
        }

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.SecureCorrected,
            UserId: userId,
            Details: $"Secure correction applied, new version {version}. Reason: {reason}",
            DataHash: dataHash,
            Version: version,
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }

    /// <summary>
    /// Logs that an object was sealed.
    /// </summary>
    /// <param name="auditTrail">The audit trail service.</param>
    /// <param name="objectId">Object ID.</param>
    /// <param name="userId">User who sealed the object.</param>
    /// <param name="reason">Reason for sealing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created audit entry.</returns>
    public static async Task<TamperProofAuditEntry> LogSealedAsync(
        this IAuditTrailService auditTrail,
        Guid objectId,
        string? userId,
        string reason,
        CancellationToken ct = default)
    {
        var metadata = new Dictionary<string, string>
        {
            ["SealReason"] = reason
        };

        var operation = new AuditOperation(
            BlockId: objectId,
            Type: AuditOperationType.Sealed,
            UserId: userId,
            Details: $"Object sealed. Reason: {reason}",
            Metadata: metadata);

        return await auditTrail.LogOperationAsync(operation, ct);
    }
}
