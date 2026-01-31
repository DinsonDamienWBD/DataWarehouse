// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using DataWarehouse.SDK.Primitives;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.TamperProof;

/// <summary>
/// Main tamper-proof storage plugin integrating all tamper-proof components.
/// Implements 5-phase write pipeline:
///   Phase 1: User transformations (compression, encryption)
///   Phase 2: Integrity hash computation
///   Phase 3: RAID sharding
///   Phase 4: Parallel 4-tier writes (Data, Metadata, WORM, Blockchain)
///   Phase 5: Blockchain anchoring
/// Read pipeline reverses these phases with verification.
/// </summary>
public class TamperProofPlugin : PluginBase
{
    private readonly IIntegrityProvider _integrity;
    private readonly IBlockchainProvider _blockchain;
    private readonly IWormStorageProvider _worm;
    private readonly IAccessLogProvider _accessLog;
    private readonly IPipelineOrchestrator _pipelineOrchestrator;
    private readonly ILogger<TamperProofPlugin> _logger;
    private readonly TamperProofConfiguration _config;

    // Storage instances for the 4 tiers
    private readonly IStorageProvider _dataStorage;
    private readonly IStorageProvider _metadataStorage;
    private readonly IStorageProvider _wormStorage;
    private readonly IStorageProvider _blockchainStorage;

    // Batching for blockchain anchors
    private readonly ConcurrentQueue<AnchorRequest> _pendingAnchors = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private DateTime _lastBatchTime = DateTime.UtcNow;

    /// <summary>
    /// Creates a new TamperProof plugin instance.
    /// </summary>
    /// <param name="config">Tamper-proof configuration.</param>
    /// <param name="integrity">Integrity hash provider.</param>
    /// <param name="blockchain">Blockchain anchoring provider.</param>
    /// <param name="worm">WORM storage provider.</param>
    /// <param name="accessLog">Access log provider.</param>
    /// <param name="pipelineOrchestrator">Pipeline orchestration for user transformations.</param>
    /// <param name="dataStorage">Data tier storage instance.</param>
    /// <param name="metadataStorage">Metadata tier storage instance.</param>
    /// <param name="wormStorage">WORM tier storage instance.</param>
    /// <param name="blockchainStorage">Blockchain tier storage instance.</param>
    /// <param name="logger">Logger instance.</param>
    public TamperProofPlugin(
        TamperProofConfiguration config,
        IIntegrityProvider integrity,
        IBlockchainProvider blockchain,
        IWormStorageProvider worm,
        IAccessLogProvider accessLog,
        IPipelineOrchestrator pipelineOrchestrator,
        IStorageProvider dataStorage,
        IStorageProvider metadataStorage,
        IStorageProvider wormStorage,
        IStorageProvider blockchainStorage,
        ILogger<TamperProofPlugin> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
        _blockchain = blockchain ?? throw new ArgumentNullException(nameof(blockchain));
        _worm = worm ?? throw new ArgumentNullException(nameof(worm));
        _accessLog = accessLog ?? throw new ArgumentNullException(nameof(accessLog));
        _pipelineOrchestrator = pipelineOrchestrator ?? throw new ArgumentNullException(nameof(pipelineOrchestrator));
        _dataStorage = dataStorage ?? throw new ArgumentNullException(nameof(dataStorage));
        _metadataStorage = metadataStorage ?? throw new ArgumentNullException(nameof(metadataStorage));
        _wormStorage = wormStorage ?? throw new ArgumentNullException(nameof(wormStorage));
        _blockchainStorage = blockchainStorage ?? throw new ArgumentNullException(nameof(blockchainStorage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Validate configuration
        var errors = _config.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid TamperProof configuration: {string.Join("; ", errors)}");
        }
    }

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.tamperproof";

    /// <inheritdoc/>
    public override string Name => "Tamper-Proof Storage Provider";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Semantic description for AI agents to understand this plugin's purpose.
    /// </summary>
    public string SemanticDescription =>
        "Provides tamper-proof storage with immutable versioning, blockchain anchoring, " +
        "RAID redundancy, and WORM compliance. All writes are logged, hashed, and verified. " +
        "Supports automatic tamper detection and recovery from WORM backups.";

    /// <summary>
    /// Semantic tags for AI-based discovery and categorization.
    /// </summary>
    public string[] SemanticTags => new[]
    {
        "tamper-proof",
        "immutable",
        "blockchain",
        "worm",
        "compliance",
        "audit",
        "integrity",
        "versioning",
        "raid"
    };

    /// <summary>
    /// Executes the 5-phase write pipeline for tamper-proof storage.
    /// </summary>
    /// <param name="data">Data stream to write.</param>
    /// <param name="writeContext">Write context with author and comment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Secure write result containing object ID, hashes, and audit trail.</returns>
    /// <exception cref="ArgumentNullException">If data or writeContext is null.</exception>
    /// <exception cref="InvalidOperationException">If write fails with strict transaction mode.</exception>
    public async Task<SecureWriteResult> ExecuteWritePipelineAsync(
        Stream data,
        WriteContext writeContext,
        CancellationToken ct = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (writeContext == null)
            throw new ArgumentNullException(nameof(writeContext));

        writeContext.ValidateOrThrow();

        var objectId = Guid.NewGuid();
        _logger.LogInformation("Starting write pipeline for object {ObjectId}", objectId);

        try
        {
            // Phase 1: Apply user transformations via pipeline orchestrator
            _logger.LogDebug("Phase 1: Applying user transformations");
            var (transformedData, pipelineStages) = await WritePhaseHandlers.ApplyUserTransformationsAsync(
                data,
                _pipelineOrchestrator,
                _logger,
                ct);

            // Phase 2: Compute integrity hash
            _logger.LogDebug("Phase 2: Computing integrity hash");
            var integrityHash = await WritePhaseHandlers.ComputeIntegrityHashAsync(
                transformedData,
                _config.HashAlgorithm,
                _integrity,
                _logger,
                ct);

            var originalSize = data.CanSeek ? data.Length : transformedData.Length;
            var finalSize = transformedData.Length;

            // Phase 3: RAID sharding
            _logger.LogDebug("Phase 3: Performing RAID sharding");
            var (shards, raidConfig) = await WritePhaseHandlers.PerformRaidShardingAsync(
                transformedData,
                objectId,
                _config.Raid,
                _integrity,
                _logger,
                ct);

            // Phase 4: Parallel writes to 4 tiers with transaction semantics
            _logger.LogDebug("Phase 4: Writing to 4 tiers (Data, Metadata, WORM, Blockchain)");
            var manifest = await CreateManifestAsync(
                objectId,
                1, // version
                writeContext,
                integrityHash,
                originalSize,
                finalSize,
                pipelineStages,
                raidConfig,
                ct);

            var transactionResult = await WritePhaseHandlers.ExecuteTransactionalWriteAsync(
                objectId,
                manifest,
                shards,
                transformedData,
                _dataStorage,
                _metadataStorage,
                _worm,
                _config,
                _logger,
                ct);

            if (!transactionResult.Success)
            {
                _logger.LogError("Transaction failed for object {ObjectId}: {Error}",
                    objectId, transactionResult.ErrorMessage);

                if (_config.TransactionFailureBehavior == TransactionFailureBehavior.Strict)
                {
                    throw new InvalidOperationException(
                        $"Write transaction failed: {transactionResult.ErrorMessage}");
                }

                return SecureWriteResult.CreateFailure(
                    transactionResult.ErrorMessage ?? "Unknown transaction failure",
                    objectId,
                    transactionResult.DegradationState);
            }

            // Phase 5: Blockchain anchoring (batched or immediate)
            _logger.LogDebug("Phase 5: Blockchain anchoring");
            string? blockchainAnchorId = null;

            if (_config.BlockchainBatching.WaitForConfirmation)
            {
                var anchorRequest = new AnchorRequest
                {
                    ObjectId = objectId,
                    Hash = integrityHash,
                    WriteContext = writeContext.ToRecord(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Version = 1
                };

                var anchor = await _blockchain.AnchorAsync(anchorRequest, ct);
                blockchainAnchorId = anchor.AnchorId;
            }
            else
            {
                // Queue for batching
                _pendingAnchors.Enqueue(new AnchorRequest
                {
                    ObjectId = objectId,
                    Hash = integrityHash,
                    WriteContext = writeContext.ToRecord(),
                    Timestamp = DateTimeOffset.UtcNow,
                    Version = 1
                });

                // Trigger batch if threshold reached
                _ = Task.Run(() => ProcessBlockchainBatchAsync(ct), ct);
            }

            // Log access
            await _accessLog.LogAccessAsync(new AccessLog
            {
                ObjectId = objectId,
                Version = 1,
                AccessType = AccessType.Write,
                Principal = writeContext.Author,
                AccessedAt = DateTimeOffset.UtcNow,
                ClientIp = writeContext.ClientIp,
                SessionId = writeContext.SessionId,
                Success = true
            }, ct);

            _logger.LogInformation("Write pipeline completed successfully for object {ObjectId}", objectId);

            return SecureWriteResult.CreateSuccess(
                objectId,
                1,
                integrityHash,
                transactionResult.MetadataTierResult?.ResourceId ?? string.Empty,
                transactionResult.WormTierResult?.ResourceId ?? string.Empty,
                writeContext.ToRecord(),
                shards.Count,
                originalSize,
                finalSize,
                blockchainAnchorId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write pipeline failed for object {ObjectId}", objectId);

            // Log failed access
            await _accessLog.LogAccessAsync(new AccessLog
            {
                ObjectId = objectId,
                Version = 0,
                AccessType = AccessType.Write,
                Principal = writeContext.Author,
                AccessedAt = DateTimeOffset.UtcNow,
                ClientIp = writeContext.ClientIp,
                SessionId = writeContext.SessionId,
                Success = false,
                ErrorMessage = ex.Message
            }, ct);

            throw;
        }
    }

    /// <summary>
    /// Executes the 5-phase read pipeline for tamper-proof storage.
    /// Verifies integrity and recovers from WORM if tampering is detected.
    /// </summary>
    /// <param name="objectId">Object ID to read.</param>
    /// <param name="version">Version to read (null for latest).</param>
    /// <param name="readMode">Verification level.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Secure read result with data and verification status.</returns>
    public async Task<SecureReadResult> ExecuteReadPipelineAsync(
        Guid objectId,
        int? version,
        ReadMode readMode,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting read pipeline for object {ObjectId} version {Version}",
            objectId, version?.ToString() ?? "latest");

        try
        {
            // Phase 1: Load manifest from metadata tier
            var manifest = await ReadPhaseHandlers.LoadManifestAsync(
                objectId,
                version,
                _metadataStorage,
                _logger,
                ct);

            if (manifest == null)
            {
                return SecureReadResult.CreateFailure(
                    objectId,
                    version ?? 0,
                    "Manifest not found",
                    readMode);
            }

            // Phase 2: Load and reconstruct shards
            var reconstructedData = await ReadPhaseHandlers.LoadAndReconstructShardsAsync(
                manifest,
                _dataStorage,
                _logger,
                ct);

            // Phase 3: Verify integrity
            var integrityResult = await ReadPhaseHandlers.VerifyIntegrityAsync(
                reconstructedData,
                manifest,
                readMode,
                _integrity,
                _logger,
                ct);

            bool recoveryPerformed = false;
            RecoveryResult? recoveryDetails = null;

            if (!integrityResult.IntegrityValid)
            {
                _logger.LogWarning("Integrity verification failed for object {ObjectId}, attempting recovery",
                    objectId);

                // Attempt recovery from WORM
                if (_config.RecoveryBehavior != TamperRecoveryBehavior.AlertAndWait)
                {
                    recoveryDetails = await ReadPhaseHandlers.RecoverFromWormAsync(
                        manifest,
                        _worm,
                        _dataStorage,
                        _logger,
                        ct);

                    if (recoveryDetails.Success)
                    {
                        recoveryPerformed = true;
                        reconstructedData = await ReadPhaseHandlers.LoadAndReconstructShardsAsync(
                            manifest,
                            _dataStorage,
                            _logger,
                            ct);
                    }
                }

                // Log tamper incident
                await LogTamperIncidentAsync(objectId, manifest.Version, integrityResult, ct);
            }

            // Phase 4: Reverse pipeline transformations
            var originalData = await ReadPhaseHandlers.ReversePipelineTransformationsAsync(
                reconstructedData,
                manifest,
                _pipelineOrchestrator,
                _logger,
                ct);

            // Phase 5: Log access
            await _accessLog.LogAccessAsync(new AccessLog
            {
                ObjectId = objectId,
                Version = manifest.Version,
                AccessType = AccessType.Read,
                Principal = "system", // TODO: Get from context
                AccessedAt = DateTimeOffset.UtcNow,
                Success = true
            }, ct);

            _logger.LogInformation("Read pipeline completed successfully for object {ObjectId}", objectId);

            return SecureReadResult.CreateSuccess(
                objectId,
                manifest.Version,
                originalData,
                integrityResult,
                readMode,
                manifest.WriteContext,
                recoveryPerformed,
                recoveryDetails);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Read pipeline failed for object {ObjectId}", objectId);

            // Log failed access
            await _accessLog.LogAccessAsync(new AccessLog
            {
                ObjectId = objectId,
                Version = version ?? 0,
                AccessType = AccessType.Read,
                Principal = "system",
                AccessedAt = DateTimeOffset.UtcNow,
                Success = false,
                ErrorMessage = ex.Message
            }, ct);

            throw;
        }
    }

    /// <summary>
    /// Creates a tamper-proof manifest for an object.
    /// </summary>
    private async Task<TamperProofManifest> CreateManifestAsync(
        Guid objectId,
        int version,
        WriteContext writeContext,
        IntegrityHash integrityHash,
        long originalSize,
        long finalSize,
        List<PipelineStageRecord> pipelineStages,
        RaidRecord raidConfig,
        CancellationToken ct)
    {
        var manifest = new TamperProofManifest
        {
            ObjectId = objectId,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            WriteContext = writeContext.ToRecord(),
            HashAlgorithm = _config.HashAlgorithm,
            OriginalContentHash = integrityHash.HashValue,
            OriginalContentSize = originalSize,
            FinalContentHash = integrityHash.HashValue,
            FinalContentSize = finalSize,
            PipelineStages = pipelineStages,
            RaidConfiguration = raidConfig,
            WormRetentionPeriod = _config.DefaultRetentionPeriod,
            WormRetentionExpiresAt = DateTimeOffset.UtcNow.Add(_config.DefaultRetentionPeriod)
        };

        // Validate manifest
        var errors = manifest.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid manifest: {string.Join("; ", errors)}");
        }

        return await Task.FromResult(manifest);
    }

    /// <summary>
    /// Processes pending blockchain anchors in batches.
    /// </summary>
    private async Task ProcessBlockchainBatchAsync(CancellationToken ct)
    {
        await _batchLock.WaitAsync(ct);
        try
        {
            var shouldBatch = _pendingAnchors.Count >= _config.BlockchainBatching.MaxBatchSize ||
                             DateTime.UtcNow - _lastBatchTime >= _config.BlockchainBatching.MaxBatchDelay;

            if (!shouldBatch || _pendingAnchors.IsEmpty)
            {
                return;
            }

            var batch = new List<AnchorRequest>();
            while (batch.Count < _config.BlockchainBatching.MaxBatchSize &&
                   _pendingAnchors.TryDequeue(out var request))
            {
                batch.Add(request);
            }

            if (batch.Count > 0)
            {
                _logger.LogInformation("Processing blockchain batch with {Count} anchors", batch.Count);

                await _blockchain.AnchorBatchAsync(batch, ct);
                _lastBatchTime = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process blockchain batch");
        }
        finally
        {
            _batchLock.Release();
        }
    }

    /// <summary>
    /// Logs a tamper incident for audit and alerting.
    /// </summary>
    private async Task LogTamperIncidentAsync(
        Guid objectId,
        int version,
        IntegrityVerificationResult integrityResult,
        CancellationToken ct)
    {
        var incident = new TamperIncident
        {
            IncidentId = Guid.NewGuid(),
            ObjectId = objectId,
            Version = version,
            DetectedAt = DateTimeOffset.UtcNow,
            TamperedComponent = "Shard data",
            ExpectedHash = integrityResult.ExpectedHash,
            ActualHash = integrityResult.ActualHash,
            AttributionConfidence = AttributionConfidence.Unknown,
            RecoveryPerformed = false,
            AdminNotified = _config.Alerts.PublishToMessageBus
        };

        _logger.LogWarning("Tamper incident detected: {Incident}", incident);

        // TODO: Publish to message bus for alerting
        await Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "TamperProof";
        metadata["SupportsPipeline"] = true;
        metadata["SupportsRAID"] = true;
        metadata["SupportsWORM"] = true;
        metadata["SupportsBlockchain"] = true;
        metadata["SupportsVersioning"] = true;
        metadata["SupportsRecovery"] = true;
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["SemanticTags"] = SemanticTags;
        return metadata;
    }
}
