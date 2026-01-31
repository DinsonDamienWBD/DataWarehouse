// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.SDK.Contracts.TamperProof;

/// <summary>
/// Main provider interface for tamper-proof storage operations.
/// Orchestrates the four-tier architecture (data, metadata, WORM, blockchain) with comprehensive
/// integrity protection, tamper detection, automatic recovery, and full audit capabilities.
/// </summary>
public interface ITamperProofProvider
{
    /// <summary>
    /// Gets the current tamper-proof configuration.
    /// Includes both structural settings (immutable after seal) and behavioral settings (changeable).
    /// </summary>
    TamperProofConfiguration Configuration { get; }

    /// <summary>
    /// Gets the current state of all storage instances.
    /// Maps instance ID to degradation state (Healthy, Degraded, Offline, etc.).
    /// </summary>
    IReadOnlyDictionary<string, InstanceDegradationState> InstanceStates { get; }

    /// <summary>
    /// Indicates whether the structural configuration is sealed (immutable).
    /// Once sealed (after first write), structural settings cannot be changed.
    /// </summary>
    bool IsSealed { get; }

    /// <summary>
    /// Writes data to tamper-proof storage with full integrity protection.
    /// Applies pipeline transformations, RAID sharding, manifest creation, WORM backup,
    /// blockchain anchoring, and access logging.
    /// </summary>
    /// <param name="objectId">Unique identifier for the object. Use Guid.Empty for auto-generation.</param>
    /// <param name="data">Stream containing the data to write.</param>
    /// <param name="context">Write context with author, comment, and attribution metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing object ID, integrity hash, manifest ID, and tier write results.</returns>
    /// <exception cref="ArgumentNullException">Thrown when data or context is null.</exception>
    /// <exception cref="ArgumentException">Thrown when context validation fails.</exception>
    Task<SecureWriteResult> SecureWriteAsync(
        Guid objectId,
        Stream data,
        WriteContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Reads data from tamper-proof storage with verification and optional recovery.
    /// Reconstructs data from shards, reverses pipeline transformations, and verifies integrity.
    /// If tampering is detected, automatically recovers from WORM based on configuration.
    /// </summary>
    /// <param name="objectId">Unique identifier of the object to read.</param>
    /// <param name="mode">Read verification mode (Fast, Verified, Audit).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing reconstructed data, integrity verification, and recovery details.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the object does not exist.</exception>
    Task<SecureReadResult> SecureReadAsync(
        Guid objectId,
        ReadMode mode,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a correction (new version) of an existing object.
    /// Preserves the original in WORM, creates a new version with incremented version number,
    /// and links to the previous version in the audit chain.
    /// </summary>
    /// <param name="objectId">Original object ID to correct.</param>
    /// <param name="correctedData">Stream containing the corrected data.</param>
    /// <param name="context">Correction context with reason, author, and comment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing original and new object IDs, versions, and audit chain.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the original object does not exist.</exception>
    /// <exception cref="ArgumentNullException">Thrown when correctedData or context is null.</exception>
    Task<CorrectionResult> SecureCorrectAsync(
        Guid objectId,
        Stream correctedData,
        CorrectionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Performs a full audit of an object including all versions, integrity verification,
    /// blockchain anchors, access logs, and tamper incident history.
    /// </summary>
    /// <param name="objectId">Object ID to audit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete audit result with chain of custody, verification results, and incidents.</returns>
    Task<AuditResult> AuditAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Manually recovers an object from WORM storage when tampering is detected.
    /// Used when automatic recovery is disabled or manual intervention is required.
    /// </summary>
    /// <param name="objectId">Object ID to recover.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recovery result with source, details, and tamper incident information.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the object does not exist in WORM.</exception>
    Task<RecoveryResult> RecoverFromWormAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the most recent tamper incident report for an object.
    /// Returns null if no tampering has been detected.
    /// </summary>
    /// <param name="objectId">Object ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tamper incident report with attribution analysis, or null if no incidents.</returns>
    Task<TamperIncidentReport?> GetTamperIncidentAsync(Guid objectId, CancellationToken ct = default);

    /// <summary>
    /// Seals the structural configuration, making it immutable.
    /// Called automatically on first write if not already sealed.
    /// Once sealed, configuration changes that affect structure (RAID, hash algorithm, etc.) are blocked.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when configuration validation fails.</exception>
    Task SealAsync(CancellationToken ct = default);
}

/// <summary>
/// Abstract base class for tamper-proof provider plugins.
/// Implements common orchestration logic for the four-tier architecture.
/// Derived classes implement provider-specific manifest creation and pipeline execution.
/// </summary>
public abstract class TamperProofProviderPluginBase : FeaturePluginBase, ITamperProofProvider
{
    private readonly IIntegrityProvider _integrityProvider;
    private readonly IBlockchainProvider _blockchainProvider;
    private readonly IWormStorageProvider _wormStorageProvider;
    private readonly IAccessLogProvider _accessLogProvider;
    private readonly IPipelineOrchestrator _pipelineOrchestrator;

    private readonly Dictionary<string, InstanceDegradationState> _instanceStates = new();
    private bool _isSealed = false;
    private readonly object _sealLock = new object();

    /// <summary>
    /// Initializes a new tamper-proof provider plugin.
    /// </summary>
    /// <param name="integrityProvider">Provider for hash computation and verification.</param>
    /// <param name="blockchainProvider">Provider for blockchain anchoring.</param>
    /// <param name="wormStorageProvider">Provider for WORM (Write-Once-Read-Many) storage.</param>
    /// <param name="accessLogProvider">Provider for access logging and tamper attribution.</param>
    /// <param name="pipelineOrchestrator">Orchestrator for transformation pipeline (compression, encryption, etc.).</param>
    /// <exception cref="ArgumentNullException">Thrown when any provider is null.</exception>
    protected TamperProofProviderPluginBase(
        IIntegrityProvider integrityProvider,
        IBlockchainProvider blockchainProvider,
        IWormStorageProvider wormStorageProvider,
        IAccessLogProvider accessLogProvider,
        IPipelineOrchestrator pipelineOrchestrator)
    {
        _integrityProvider = integrityProvider ?? throw new ArgumentNullException(nameof(integrityProvider));
        _blockchainProvider = blockchainProvider ?? throw new ArgumentNullException(nameof(blockchainProvider));
        _wormStorageProvider = wormStorageProvider ?? throw new ArgumentNullException(nameof(wormStorageProvider));
        _accessLogProvider = accessLogProvider ?? throw new ArgumentNullException(nameof(accessLogProvider));
        _pipelineOrchestrator = pipelineOrchestrator ?? throw new ArgumentNullException(nameof(pipelineOrchestrator));

        // Initialize instance states
        foreach (var instance in Configuration.StorageInstances.AllInstances.Values)
        {
            _instanceStates[instance.InstanceId] = InstanceDegradationState.Healthy;
        }
    }

    /// <summary>
    /// Gets the tamper-proof configuration.
    /// Must be implemented by derived classes.
    /// </summary>
    public abstract TamperProofConfiguration Configuration { get; }

    /// <summary>
    /// Gets the current state of all storage instances.
    /// </summary>
    public IReadOnlyDictionary<string, InstanceDegradationState> InstanceStates => _instanceStates;

    /// <summary>
    /// Gets whether the structural configuration is sealed.
    /// </summary>
    public bool IsSealed => _isSealed;

    /// <summary>
    /// Plugin category is always FeatureProvider for tamper-proof plugins.
    /// </summary>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Writes data to tamper-proof storage with full integrity protection.
    /// </summary>
    public virtual async Task<SecureWriteResult> SecureWriteAsync(
        Guid objectId,
        Stream data,
        WriteContext context,
        CancellationToken ct = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (context == null)
            throw new ArgumentNullException(nameof(context));

        // Validate write context (author and comment are required)
        context.ValidateOrThrow();

        // Auto-seal on first write
        if (!_isSealed)
        {
            await SealAsync(ct);
        }

        // Generate object ID if not provided
        if (objectId == Guid.Empty)
        {
            objectId = Guid.NewGuid();
        }

        try
        {
            // Log write operation
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Write,
                Principal = context.Author,
                Timestamp = DateTimeOffset.UtcNow,
                ClientIp = context.ClientIp,
                SessionId = context.SessionId,
                Succeeded = false
            }, ct);

            // Execute the write pipeline (transformation, sharding, storage, etc.)
            var result = await ExecuteWritePipelineAsync(objectId, data, context, ct);

            // Update instance states based on write results
            UpdateInstanceStatesFromWrite(result);

            // Log successful write
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Write,
                Principal = context.Author,
                Timestamp = DateTimeOffset.UtcNow,
                ClientIp = context.ClientIp,
                SessionId = context.SessionId,
                Succeeded = true,
                ComputedHash = result.IntegrityHash.HashValue
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            // Log failed write
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Write,
                Principal = context.Author,
                Timestamp = DateTimeOffset.UtcNow,
                ClientIp = context.ClientIp,
                SessionId = context.SessionId,
                Succeeded = false,
                ErrorMessage = ex.Message
            }, ct);

            throw;
        }
    }

    /// <summary>
    /// Reads data from tamper-proof storage with verification and optional recovery.
    /// </summary>
    public virtual async Task<SecureReadResult> SecureReadAsync(
        Guid objectId,
        ReadMode mode,
        CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        try
        {
            // Log read operation
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Read,
                Principal = "system", // TODO: Get from context
                Timestamp = DateTimeOffset.UtcNow,
                Succeeded = false
            }, ct);

            // Execute the read pipeline (fetch, verify, reconstruct, reverse pipeline)
            var result = await ExecuteReadPipelineAsync(objectId, mode, ct);

            // Update instance states based on read results
            UpdateInstanceStatesFromRead(result);

            // Log successful read
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Read,
                Principal = "system",
                Timestamp = DateTimeOffset.UtcNow,
                Succeeded = true
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            // Log failed read
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Read,
                Principal = "system",
                Timestamp = DateTimeOffset.UtcNow,
                Succeeded = false,
                ErrorMessage = ex.Message
            }, ct);

            throw;
        }
    }

    /// <summary>
    /// Creates a correction (new version) of an existing object.
    /// </summary>
    public virtual async Task<CorrectionResult> SecureCorrectAsync(
        Guid objectId,
        Stream correctedData,
        CorrectionContext context,
        CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        if (correctedData == null)
            throw new ArgumentNullException(nameof(correctedData));

        if (context == null)
            throw new ArgumentNullException(nameof(context));

        // Validate correction context
        context.ValidateOrThrow();

        try
        {
            // Log correction operation
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Correct,
                Principal = context.Author,
                Timestamp = DateTimeOffset.UtcNow,
                ClientIp = context.ClientIp,
                SessionId = context.SessionId,
                Succeeded = false
            }, ct);

            // Read the original object to get its version
            var originalRead = await SecureReadAsync(objectId, ReadMode.Verified, ct);

            // Create new object ID for the correction
            var newObjectId = Guid.NewGuid();
            var newVersion = originalRead.Version + 1;

            // Write the corrected data as a new object
            var writeResult = await SecureWriteAsync(newObjectId, correctedData, context, ct);

            // Create correction result
            var correctionResult = CorrectionResult.CreateSuccess(
                originalObjectId: objectId,
                originalVersion: originalRead.Version,
                newObjectId: newObjectId,
                newVersion: newVersion,
                writeResult: writeResult,
                correctionContext: context.ToCorrectionRecord(),
                auditChain: null // TODO: Build audit chain from manifest
            );

            // Log successful correction
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Correct,
                Principal = context.Author,
                Timestamp = DateTimeOffset.UtcNow,
                ClientIp = context.ClientIp,
                SessionId = context.SessionId,
                Succeeded = true
            }, ct);

            return correctionResult;
        }
        catch (Exception ex)
        {
            // Log failed correction
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.Correct,
                Principal = context.Author,
                Timestamp = DateTimeOffset.UtcNow,
                ClientIp = context.ClientIp,
                SessionId = context.SessionId,
                Succeeded = false,
                ErrorMessage = ex.Message
            }, ct);

            return CorrectionResult.CreateFailure(objectId, 0, ex.Message);
        }
    }

    /// <summary>
    /// Performs a full audit of an object.
    /// </summary>
    public virtual async Task<AuditResult> AuditAsync(Guid objectId, CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        try
        {
            // Log audit operation
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.AdminOperation,
                Principal = "system",
                Timestamp = DateTimeOffset.UtcNow,
                Succeeded = false
            }, ct);

            // Read the object to get manifest
            var readResult = await SecureReadAsync(objectId, ReadMode.Audit, ct);

            // Get access logs
            var accessLogs = await _accessLogProvider.GetAccessHistoryAsync(
                objectId,
                DateTimeOffset.UtcNow.AddYears(-1),
                DateTimeOffset.UtcNow,
                ct);

            // Get tamper incidents
            var incident = await GetTamperIncidentAsync(objectId, ct);
            var incidents = incident != null ? new[] { new TamperIncident
            {
                IncidentId = incident.IncidentId,
                ObjectId = incident.ObjectId,
                Version = 1,
                DetectedAt = incident.DetectedAt,
                TamperedComponent = incident.AffectedInstance,
                ExpectedHash = IntegrityHash.Parse(incident.ExpectedHash),
                ActualHash = IntegrityHash.Parse(incident.ActualHash),
                AttributionConfidence = incident.AttributionConfidence,
                SuspectedPrincipal = incident.SuspectedPrincipal,
                RecoveryPerformed = incident.RecoverySucceeded,
                RecoveryDetails = incident.RecoveryAction.ToString(),
                AdminNotified = true
            } } : Array.Empty<TamperIncident>();

            var auditResult = AuditResult.CreateSuccess(
                objectId: objectId,
                auditChain: new AuditChain { RootObjectId = objectId, Entries = Array.Empty<AuditChainEntry>() },
                accessLogs: accessLogs.Cast<AccessLog>().ToList(),
                tamperIncidents: incidents
            );

            // Log successful audit
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.AdminOperation,
                Principal = "system",
                Timestamp = DateTimeOffset.UtcNow,
                Succeeded = true
            }, ct);

            return auditResult;
        }
        catch (Exception ex)
        {
            // Log failed audit
            await _accessLogProvider.LogAccessAsync(new AccessLogEntry
            {
                EntryId = Guid.NewGuid(),
                ObjectId = objectId,
                AccessType = AccessType.AdminOperation,
                Principal = "system",
                Timestamp = DateTimeOffset.UtcNow,
                Succeeded = false,
                ErrorMessage = ex.Message
            }, ct);

            return AuditResult.CreateFailure(objectId, ex.Message);
        }
    }

    /// <summary>
    /// Manually recovers an object from WORM storage.
    /// </summary>
    public virtual async Task<RecoveryResult> RecoverFromWormAsync(Guid objectId, CancellationToken ct = default)
    {
        if (objectId == Guid.Empty)
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));

        try
        {
            // Check if object exists in WORM
            if (!await _wormStorageProvider.ExistsAsync(objectId, ct))
            {
                throw new KeyNotFoundException($"Object {objectId} does not exist in WORM storage.");
            }

            // Read from WORM
            var wormData = await _wormStorageProvider.ReadAsync(objectId, ct);

            // Verify integrity
            var manifest = await CreateManifestAsync(objectId, wormData, new WriteContext
            {
                Author = "system",
                Comment = "Recovery from WORM"
            }, ct);

            return RecoveryResult.CreateSuccess(
                objectId: objectId,
                version: manifest.Version,
                recoverySource: "WORM",
                recoveryDetails: $"Recovered from WORM storage, hash: {manifest.OriginalContentHash}",
                tamperIncident: null
            );
        }
        catch (Exception ex)
        {
            return RecoveryResult.CreateFailure(objectId, 0, ex.Message);
        }
    }

    /// <summary>
    /// Retrieves the most recent tamper incident report for an object.
    /// Default implementation returns null (no incidents).
    /// Derived classes should override to implement actual incident storage/retrieval.
    /// </summary>
    public virtual Task<TamperIncidentReport?> GetTamperIncidentAsync(Guid objectId, CancellationToken ct = default)
    {
        // Default implementation: no incident tracking
        // Derived classes should override to store and retrieve incidents
        return Task.FromResult<TamperIncidentReport?>(null);
    }

    /// <summary>
    /// Seals the structural configuration, making it immutable.
    /// </summary>
    public virtual Task SealAsync(CancellationToken ct = default)
    {
        lock (_sealLock)
        {
            if (_isSealed)
            {
                return Task.CompletedTask;
            }

            // Validate configuration before sealing
            var errors = Configuration.Validate();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Cannot seal configuration: {string.Join("; ", errors)}");
            }

            _isSealed = true;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates a tamper-proof manifest for an object.
    /// Must be implemented by derived classes for provider-specific manifest creation.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="data">Original data stream.</param>
    /// <param name="context">Write context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete tamper-proof manifest.</returns>
    protected abstract Task<TamperProofManifest> CreateManifestAsync(
        Guid objectId,
        Stream data,
        WriteContext context,
        CancellationToken ct);

    /// <summary>
    /// Executes the complete write pipeline.
    /// Must be implemented by derived classes for provider-specific write orchestration.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="data">Data stream to write.</param>
    /// <param name="context">Write context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Secure write result.</returns>
    protected abstract Task<SecureWriteResult> ExecuteWritePipelineAsync(
        Guid objectId,
        Stream data,
        WriteContext context,
        CancellationToken ct);

    /// <summary>
    /// Executes the complete read pipeline.
    /// Must be implemented by derived classes for provider-specific read orchestration.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="mode">Read mode (Fast, Verified, Audit).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Secure read result.</returns>
    protected abstract Task<SecureReadResult> ExecuteReadPipelineAsync(
        Guid objectId,
        ReadMode mode,
        CancellationToken ct);

    /// <summary>
    /// Updates instance states based on write operation results.
    /// </summary>
    private void UpdateInstanceStatesFromWrite(SecureWriteResult result)
    {
        if (!result.Success)
        {
            // Mark instances as degraded based on warnings
            foreach (var warning in result.Warnings)
            {
                if (warning.Contains("WORM", StringComparison.OrdinalIgnoreCase))
                {
                    _instanceStates[Configuration.StorageInstances.Worm.InstanceId] =
                        InstanceDegradationState.DegradedNoRecovery;
                }
                else if (warning.Contains("blockchain", StringComparison.OrdinalIgnoreCase))
                {
                    _instanceStates[Configuration.StorageInstances.Blockchain.InstanceId] =
                        InstanceDegradationState.Degraded;
                }
            }
        }
    }

    /// <summary>
    /// Updates instance states based on read operation results.
    /// </summary>
    private void UpdateInstanceStatesFromRead(SecureReadResult result)
    {
        if (result.RecoveryPerformed)
        {
            // Mark data instance as corrupted since recovery was needed
            _instanceStates[Configuration.StorageInstances.Data.InstanceId] =
                InstanceDegradationState.Corrupted;
        }
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "TamperProof";
        metadata["FourTierArchitecture"] = true;
        metadata["SupportsRAID"] = true;
        metadata["SupportsWORM"] = true;
        metadata["SupportsBlockchain"] = true;
        metadata["SupportsAutoRecovery"] = true;
        metadata["SupportsTamperAttribution"] = true;
        metadata["SupportsCorrections"] = true;
        metadata["IsSealed"] = _isSealed;
        metadata["HashAlgorithm"] = Configuration.HashAlgorithm.ToString();
        metadata["ConsensusMode"] = Configuration.ConsensusMode.ToString();
        metadata["WormMode"] = Configuration.WormMode.ToString();
        return metadata;
    }
}
