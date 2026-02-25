using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Provenance;

#region Enums and Core Types

/// <summary>
/// Type of transformation applied to knowledge.
/// </summary>
public enum TransformationType
{
    /// <summary>Knowledge was created from scratch.</summary>
    Creation,

    /// <summary>Knowledge was imported from external source.</summary>
    Import,

    /// <summary>Knowledge was derived from other knowledge.</summary>
    Derivation,

    /// <summary>Knowledge was merged from multiple sources.</summary>
    Merge,

    /// <summary>Knowledge was split into parts.</summary>
    Split,

    /// <summary>Knowledge was transformed/enriched.</summary>
    Transformation,

    /// <summary>Knowledge was aggregated from multiple items.</summary>
    Aggregation,

    /// <summary>Knowledge was filtered/pruned.</summary>
    Filtering,

    /// <summary>Knowledge was normalized/standardized.</summary>
    Normalization,

    /// <summary>Knowledge was anonymized.</summary>
    Anonymization,

    /// <summary>Knowledge was redacted.</summary>
    Redaction,

    /// <summary>Knowledge was corrected.</summary>
    Correction,

    /// <summary>Knowledge was versioned.</summary>
    Versioning,

    /// <summary>Knowledge was migrated between systems.</summary>
    Migration,

    /// <summary>Knowledge was replicated to another location.</summary>
    Replication
}

/// <summary>
/// Trust level for knowledge.
/// </summary>
public enum TrustLevel
{
    /// <summary>Untrusted or unverified.</summary>
    Untrusted = 0,

    /// <summary>Low trust (single source, unverified).</summary>
    Low = 1,

    /// <summary>Medium trust (verified source or multiple sources).</summary>
    Medium = 2,

    /// <summary>High trust (verified and signed).</summary>
    High = 3,

    /// <summary>Maximum trust (cryptographically verified chain).</summary>
    Maximum = 4
}

/// <summary>
/// Type of audit action.
/// </summary>
public enum AuditAction
{
    /// <summary>Knowledge was created.</summary>
    Create,

    /// <summary>Knowledge was read/accessed.</summary>
    Read,

    /// <summary>Knowledge was updated.</summary>
    Update,

    /// <summary>Knowledge was deleted.</summary>
    Delete,

    /// <summary>Knowledge was queried.</summary>
    Query,

    /// <summary>Knowledge was exported.</summary>
    Export,

    /// <summary>Knowledge was shared with another instance.</summary>
    Share,

    /// <summary>Knowledge signature was verified.</summary>
    SignatureVerify,

    /// <summary>Knowledge access was denied.</summary>
    AccessDenied,

    /// <summary>Knowledge tampering was detected.</summary>
    TamperDetected
}

/// <summary>
/// Signature algorithm for knowledge signing.
/// </summary>
public enum SignatureAlgorithm
{
    /// <summary>ECDSA with P-256 curve and SHA-256.</summary>
    EcdsaP256Sha256,

    /// <summary>ECDSA with P-384 curve and SHA-384.</summary>
    EcdsaP384Sha384,

    /// <summary>Ed25519 signature.</summary>
    Ed25519,

    /// <summary>RSA with PSS padding and SHA-256.</summary>
    RsaPssSha256,

    /// <summary>RSA with PKCS#1 v1.5 padding and SHA-256.</summary>
    RsaPkcs1Sha256
}

#endregion

#region Provenance Records

/// <summary>
/// Complete provenance record for a piece of knowledge.
/// </summary>
public sealed record ProvenanceRecord
{
    /// <summary>Unique identifier for this provenance record.</summary>
    public string ProvenanceId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>ID of the knowledge this provenance belongs to.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Original source of the knowledge.</summary>
    public required KnowledgeOrigin Origin { get; init; }

    /// <summary>Chain of transformations applied to the knowledge.</summary>
    public List<TransformationRecord> Transformations { get; init; } = new();

    /// <summary>Current version of the knowledge.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Cryptographic signature of the knowledge.</summary>
    public KnowledgeSignature? Signature { get; init; }

    /// <summary>Trust score (0.0 - 1.0).</summary>
    public double TrustScore { get; init; } = 1.0;

    /// <summary>Trust level classification.</summary>
    public TrustLevel TrustLevel { get; init; } = TrustLevel.Medium;

    /// <summary>When the provenance was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the provenance was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Hash of the knowledge content for integrity verification.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Hash algorithm used for content hash.</summary>
    public string ContentHashAlgorithm { get; init; } = "SHA-256";

    /// <summary>Parent provenance IDs (for derived knowledge).</summary>
    public List<string> ParentProvenanceIds { get; init; } = new();

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Origin information for knowledge.
/// </summary>
public sealed record KnowledgeOrigin
{
    /// <summary>Unique identifier of the origin source.</summary>
    public required string SourceId { get; init; }

    /// <summary>Type of source (e.g., "user", "system", "import", "federation").</summary>
    public required string SourceType { get; init; }

    /// <summary>Human-readable source name.</summary>
    public string? SourceName { get; init; }

    /// <summary>Instance ID where knowledge originated.</summary>
    public string? InstanceId { get; init; }

    /// <summary>User or system that created the knowledge.</summary>
    public string? Creator { get; init; }

    /// <summary>When the knowledge was originally created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>External reference (URL, file path, etc.).</summary>
    public string? ExternalReference { get; init; }

    /// <summary>Version at the source.</summary>
    public string? SourceVersion { get; init; }

    /// <summary>Whether the origin has been verified.</summary>
    public bool Verified { get; init; }

    /// <summary>Verification timestamp.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Additional origin metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Record of a transformation applied to knowledge.
/// </summary>
public sealed record TransformationRecord
{
    /// <summary>Unique identifier for this transformation.</summary>
    public string TransformationId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Type of transformation.</summary>
    public TransformationType Type { get; init; }

    /// <summary>Description of the transformation.</summary>
    public string? Description { get; init; }

    /// <summary>When the transformation occurred.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Who/what performed the transformation.</summary>
    public required string Actor { get; init; }

    /// <summary>Actor type (user, system, pipeline, etc.).</summary>
    public string ActorType { get; init; } = "system";

    /// <summary>Instance ID where transformation occurred.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Input knowledge IDs.</summary>
    public List<string> InputKnowledgeIds { get; init; } = new();

    /// <summary>Parameters used for the transformation.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>Content hash before transformation.</summary>
    public string? InputHash { get; init; }

    /// <summary>Content hash after transformation.</summary>
    public string? OutputHash { get; init; }

    /// <summary>Whether the transformation is reversible.</summary>
    public bool IsReversible { get; init; }

    /// <summary>Reverse transformation details (if reversible).</summary>
    public Dictionary<string, object>? ReverseDetails { get; init; }
}

/// <summary>
/// Cryptographic signature for knowledge.
/// </summary>
public sealed record KnowledgeSignature
{
    /// <summary>Signature value (base64 encoded).</summary>
    public required string Signature { get; init; }

    /// <summary>Signature algorithm used.</summary>
    public SignatureAlgorithm Algorithm { get; init; }

    /// <summary>Key ID used for signing.</summary>
    public required string KeyId { get; init; }

    /// <summary>When the signature was created.</summary>
    public DateTimeOffset SignedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Signer identity.</summary>
    public string? Signer { get; init; }

    /// <summary>Certificate chain (if applicable).</summary>
    public string[]? CertificateChain { get; init; }

    /// <summary>Data that was signed (canonicalized content hash).</summary>
    public string? SignedData { get; init; }
}

#endregion

#region Provenance Recorder

/// <summary>
/// Records and tracks knowledge provenance throughout its lifecycle.
/// Thread-safe implementation with support for async operations.
/// </summary>
public sealed class ProvenanceRecorder : IAsyncDisposable
{
    private readonly BoundedDictionary<string, ProvenanceRecord> _records = new BoundedDictionary<string, ProvenanceRecord>(1000);
    private readonly BoundedDictionary<string, List<TransformationRecord>> _pendingTransformations = new BoundedDictionary<string, List<TransformationRecord>>(1000);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly IProvenanceStore? _store;
    private bool _disposed;

    /// <summary>
    /// Event raised when a provenance record is created.
    /// </summary>
    public event EventHandler<ProvenanceRecord>? ProvenanceCreated;

    /// <summary>
    /// Event raised when a transformation is recorded.
    /// </summary>
    public event EventHandler<TransformationRecord>? TransformationRecorded;

    /// <summary>
    /// Creates a new provenance recorder.
    /// </summary>
    /// <param name="store">Optional persistence store for provenance records.</param>
    public ProvenanceRecorder(IProvenanceStore? store = null)
    {
        _store = store;
    }

    /// <summary>
    /// Records the creation of new knowledge.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="origin">Origin information.</param>
    /// <param name="content">Knowledge content for hashing.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created provenance record.</returns>
    public async Task<ProvenanceRecord> RecordCreationAsync(
        string knowledgeId,
        KnowledgeOrigin origin,
        byte[] content,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeId);
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(content);

        var contentHash = ComputeHash(content);

        var record = new ProvenanceRecord
        {
            KnowledgeId = knowledgeId,
            Origin = origin,
            ContentHash = contentHash,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Transformations = new List<TransformationRecord>
            {
                new TransformationRecord
                {
                    Type = TransformationType.Creation,
                    Actor = origin.Creator ?? "system",
                    ActorType = origin.SourceType,
                    InstanceId = origin.InstanceId,
                    OutputHash = contentHash,
                    Description = "Initial creation"
                }
            }
        };

        await StoreRecordAsync(record, ct);

        ProvenanceCreated?.Invoke(this, record);

        return record;
    }

    /// <summary>
    /// Records an import of knowledge from an external source.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="origin">Origin information.</param>
    /// <param name="content">Imported content.</param>
    /// <param name="importDetails">Import details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created provenance record.</returns>
    public async Task<ProvenanceRecord> RecordImportAsync(
        string knowledgeId,
        KnowledgeOrigin origin,
        byte[] content,
        Dictionary<string, object>? importDetails = null,
        CancellationToken ct = default)
    {
        var contentHash = ComputeHash(content);

        var record = new ProvenanceRecord
        {
            KnowledgeId = knowledgeId,
            Origin = origin,
            ContentHash = contentHash,
            TrustLevel = TrustLevel.Low, // Imported content starts with low trust
            TrustScore = 0.5,
            Transformations = new List<TransformationRecord>
            {
                new TransformationRecord
                {
                    Type = TransformationType.Import,
                    Actor = origin.Creator ?? "system",
                    ActorType = "import",
                    InstanceId = origin.InstanceId,
                    OutputHash = contentHash,
                    Parameters = importDetails ?? new Dictionary<string, object>(),
                    Description = $"Imported from {origin.SourceName ?? origin.SourceId}"
                }
            }
        };

        await StoreRecordAsync(record, ct);

        ProvenanceCreated?.Invoke(this, record);

        return record;
    }

    /// <summary>
    /// Records a transformation applied to knowledge.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="transformation">Transformation record.</param>
    /// <param name="newContent">Content after transformation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated provenance record.</returns>
    public async Task<ProvenanceRecord> RecordTransformationAsync(
        string knowledgeId,
        TransformationRecord transformation,
        byte[] newContent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(knowledgeId);
        ArgumentNullException.ThrowIfNull(transformation);

        if (!_records.TryGetValue(knowledgeId, out var existing))
        {
            throw new InvalidOperationException($"No provenance record exists for knowledge '{knowledgeId}'");
        }

        var newHash = ComputeHash(newContent);
        var updatedTransformation = transformation with
        {
            InputHash = existing.ContentHash,
            OutputHash = newHash
        };

        var updatedTransformations = new List<TransformationRecord>(existing.Transformations)
        {
            updatedTransformation
        };

        var updatedRecord = existing with
        {
            ContentHash = newHash,
            Version = existing.Version + 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            Transformations = updatedTransformations,
            Signature = null // Signature invalidated by transformation
        };

        await StoreRecordAsync(updatedRecord, ct);

        TransformationRecorded?.Invoke(this, updatedTransformation);

        return updatedRecord;
    }

    /// <summary>
    /// Records derivation of new knowledge from existing knowledge.
    /// </summary>
    /// <param name="newKnowledgeId">New knowledge ID.</param>
    /// <param name="parentKnowledgeIds">Parent knowledge IDs.</param>
    /// <param name="content">Derived content.</param>
    /// <param name="derivationDetails">Derivation details.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created provenance record for derived knowledge.</returns>
    public async Task<ProvenanceRecord> RecordDerivationAsync(
        string newKnowledgeId,
        IEnumerable<string> parentKnowledgeIds,
        byte[] content,
        Dictionary<string, object>? derivationDetails = null,
        CancellationToken ct = default)
    {
        var parentIds = parentKnowledgeIds.ToList();
        var parentProvenances = parentIds
            .Select(id => _records.TryGetValue(id, out var p) ? p : null)
            .Where(p => p != null)
            .ToList();

        // Calculate trust based on parent trust scores
        var avgTrust = parentProvenances.Count > 0
            ? parentProvenances.Average(p => p!.TrustScore)
            : 0.5;

        var contentHash = ComputeHash(content);

        var origin = new KnowledgeOrigin
        {
            SourceId = $"derived:{string.Join(",", parentIds)}",
            SourceType = "derivation",
            Creator = "system",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var record = new ProvenanceRecord
        {
            KnowledgeId = newKnowledgeId,
            Origin = origin,
            ContentHash = contentHash,
            TrustScore = avgTrust * 0.95, // Slight trust degradation for derived content
            TrustLevel = avgTrust >= 0.8 ? TrustLevel.High
                : avgTrust >= 0.5 ? TrustLevel.Medium
                : TrustLevel.Low,
            ParentProvenanceIds = parentProvenances.Select(p => p!.ProvenanceId).ToList(),
            Transformations = new List<TransformationRecord>
            {
                new TransformationRecord
                {
                    Type = TransformationType.Derivation,
                    Actor = "system",
                    InputKnowledgeIds = parentIds,
                    OutputHash = contentHash,
                    Parameters = derivationDetails ?? new Dictionary<string, object>(),
                    Description = $"Derived from {parentIds.Count} parent(s)"
                }
            }
        };

        await StoreRecordAsync(record, ct);

        ProvenanceCreated?.Invoke(this, record);

        return record;
    }

    /// <summary>
    /// Records a merge of multiple knowledge items.
    /// </summary>
    /// <param name="newKnowledgeId">New knowledge ID.</param>
    /// <param name="sourceKnowledgeIds">Source knowledge IDs being merged.</param>
    /// <param name="content">Merged content.</param>
    /// <param name="mergeStrategy">Strategy used for merging.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Created provenance record.</returns>
    public async Task<ProvenanceRecord> RecordMergeAsync(
        string newKnowledgeId,
        IEnumerable<string> sourceKnowledgeIds,
        byte[] content,
        string mergeStrategy = "default",
        CancellationToken ct = default)
    {
        var sourceIds = sourceKnowledgeIds.ToList();
        var sourceProvenances = sourceIds
            .Select(id => _records.TryGetValue(id, out var p) ? p : null)
            .Where(p => p != null)
            .ToList();

        // Trust for merged content is the minimum of sources
        var minTrust = sourceProvenances.Count > 0
            ? sourceProvenances.Min(p => p!.TrustScore)
            : 0.5;

        var contentHash = ComputeHash(content);

        var origin = new KnowledgeOrigin
        {
            SourceId = $"merge:{string.Join(",", sourceIds)}",
            SourceType = "merge",
            Creator = "system"
        };

        var record = new ProvenanceRecord
        {
            KnowledgeId = newKnowledgeId,
            Origin = origin,
            ContentHash = contentHash,
            TrustScore = minTrust,
            ParentProvenanceIds = sourceProvenances.Select(p => p!.ProvenanceId).ToList(),
            Transformations = new List<TransformationRecord>
            {
                new TransformationRecord
                {
                    Type = TransformationType.Merge,
                    Actor = "system",
                    InputKnowledgeIds = sourceIds,
                    OutputHash = contentHash,
                    Parameters = new Dictionary<string, object>
                    {
                        ["mergeStrategy"] = mergeStrategy,
                        ["sourceCount"] = sourceIds.Count
                    },
                    Description = $"Merged {sourceIds.Count} sources using {mergeStrategy} strategy"
                }
            }
        };

        await StoreRecordAsync(record, ct);

        return record;
    }

    /// <summary>
    /// Gets a provenance record by knowledge ID.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <returns>Provenance record, or null if not found.</returns>
    public ProvenanceRecord? GetRecord(string knowledgeId)
    {
        _records.TryGetValue(knowledgeId, out var record);
        return record;
    }

    /// <summary>
    /// Gets all provenance records.
    /// </summary>
    public IEnumerable<ProvenanceRecord> GetAllRecords()
    {
        return _records.Values;
    }

    /// <summary>
    /// Gets provenance records by origin source.
    /// </summary>
    /// <param name="sourceId">Source ID.</param>
    /// <returns>Matching provenance records.</returns>
    public IEnumerable<ProvenanceRecord> GetBySource(string sourceId)
    {
        return _records.Values.Where(r => r.Origin.SourceId == sourceId);
    }

    private async Task StoreRecordAsync(ProvenanceRecord record, CancellationToken ct)
    {
        _records[record.KnowledgeId] = record;

        if (_store != null)
        {
            await _store.SaveAsync(record, ct);
        }
    }

    private static string ComputeHash(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToBase64String(hash);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _writeLock.Dispose();

        if (_store is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }
}

/// <summary>
/// Interface for provenance persistence.
/// </summary>
public interface IProvenanceStore
{
    /// <summary>Saves a provenance record.</summary>
    Task SaveAsync(ProvenanceRecord record, CancellationToken ct = default);

    /// <summary>Gets a provenance record by ID.</summary>
    Task<ProvenanceRecord?> GetAsync(string provenanceId, CancellationToken ct = default);

    /// <summary>Gets provenance by knowledge ID.</summary>
    Task<ProvenanceRecord?> GetByKnowledgeIdAsync(string knowledgeId, CancellationToken ct = default);

    /// <summary>Deletes a provenance record.</summary>
    Task DeleteAsync(string provenanceId, CancellationToken ct = default);

    /// <summary>Queries provenance records.</summary>
    IAsyncEnumerable<ProvenanceRecord> QueryAsync(ProvenanceQuery query, CancellationToken ct = default);
}

/// <summary>
/// Query parameters for provenance records.
/// </summary>
public sealed record ProvenanceQuery
{
    /// <summary>Filter by source type.</summary>
    public string? SourceType { get; init; }

    /// <summary>Filter by minimum trust score.</summary>
    public double? MinTrustScore { get; init; }

    /// <summary>Filter by trust level.</summary>
    public TrustLevel? TrustLevel { get; init; }

    /// <summary>Filter by creation time after.</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Filter by creation time before.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>Filter by transformation type.</summary>
    public TransformationType? TransformationType { get; init; }

    /// <summary>Maximum results.</summary>
    public int MaxResults { get; init; } = 100;
}

#endregion

#region Transformation Tracker

/// <summary>
/// Tracks and manages knowledge transformations.
/// Provides pipeline tracking and transformation history.
/// </summary>
public sealed class TransformationTracker
{
    private readonly BoundedDictionary<string, TransformationPipeline> _activePipelines = new BoundedDictionary<string, TransformationPipeline>(1000);
    private readonly BoundedDictionary<string, List<TransformationRecord>> _history = new BoundedDictionary<string, List<TransformationRecord>>(1000);
    private readonly ProvenanceRecorder _recorder;

    /// <summary>
    /// Creates a new transformation tracker.
    /// </summary>
    /// <param name="recorder">Provenance recorder.</param>
    public TransformationTracker(ProvenanceRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
    }

    /// <summary>
    /// Starts a transformation pipeline.
    /// </summary>
    /// <param name="pipelineId">Pipeline ID.</param>
    /// <param name="knowledgeId">Knowledge ID being transformed.</param>
    /// <param name="description">Pipeline description.</param>
    /// <returns>Transformation pipeline context.</returns>
    public TransformationPipeline StartPipeline(string pipelineId, string knowledgeId, string? description = null)
    {
        var pipeline = new TransformationPipeline
        {
            PipelineId = pipelineId,
            KnowledgeId = knowledgeId,
            Description = description,
            StartedAt = DateTimeOffset.UtcNow,
            Status = PipelineStatus.Running
        };

        _activePipelines[pipelineId] = pipeline;
        return pipeline;
    }

    /// <summary>
    /// Records a step in a transformation pipeline.
    /// </summary>
    /// <param name="pipelineId">Pipeline ID.</param>
    /// <param name="step">Transformation step.</param>
    public void RecordStep(string pipelineId, TransformationStep step)
    {
        if (!_activePipelines.TryGetValue(pipelineId, out var pipeline))
        {
            throw new InvalidOperationException($"Pipeline '{pipelineId}' is not active");
        }

        pipeline.Steps.Add(step);
    }

    /// <summary>
    /// Completes a transformation pipeline.
    /// </summary>
    /// <param name="pipelineId">Pipeline ID.</param>
    /// <param name="finalContent">Final content after all transformations.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Completed pipeline with provenance updates.</returns>
    public async Task<TransformationPipeline> CompletePipelineAsync(
        string pipelineId,
        byte[] finalContent,
        CancellationToken ct = default)
    {
        if (!_activePipelines.TryRemove(pipelineId, out var pipeline))
        {
            throw new InvalidOperationException($"Pipeline '{pipelineId}' is not active");
        }

        pipeline.CompletedAt = DateTimeOffset.UtcNow;
        pipeline.Status = PipelineStatus.Completed;

        // Record all transformations to provenance
        foreach (var step in pipeline.Steps)
        {
            var transformation = new TransformationRecord
            {
                Type = step.TransformationType,
                Actor = step.Actor,
                ActorType = step.ActorType,
                Description = step.Description,
                Parameters = step.Parameters,
                Timestamp = step.Timestamp
            };

            // Only record final transformation with content
            if (step == pipeline.Steps.Last())
            {
                await _recorder.RecordTransformationAsync(
                    pipeline.KnowledgeId,
                    transformation,
                    finalContent,
                    ct);
            }
        }

        // Store in history
        var historyList = _history.GetOrAdd(pipeline.KnowledgeId, _ => new List<TransformationRecord>());
        historyList.AddRange(pipeline.Steps.Select(s => new TransformationRecord
        {
            Type = s.TransformationType,
            Actor = s.Actor,
            Timestamp = s.Timestamp,
            Description = s.Description
        }));

        return pipeline;
    }

    /// <summary>
    /// Fails a transformation pipeline.
    /// </summary>
    /// <param name="pipelineId">Pipeline ID.</param>
    /// <param name="error">Error message.</param>
    /// <returns>Failed pipeline.</returns>
    public TransformationPipeline FailPipeline(string pipelineId, string error)
    {
        if (!_activePipelines.TryRemove(pipelineId, out var pipeline))
        {
            throw new InvalidOperationException($"Pipeline '{pipelineId}' is not active");
        }

        pipeline.CompletedAt = DateTimeOffset.UtcNow;
        pipeline.Status = PipelineStatus.Failed;
        pipeline.Error = error;

        return pipeline;
    }

    /// <summary>
    /// Gets transformation history for a knowledge item.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <returns>Transformation history.</returns>
    public IEnumerable<TransformationRecord> GetHistory(string knowledgeId)
    {
        if (_history.TryGetValue(knowledgeId, out var history))
        {
            return history.AsReadOnly();
        }
        return Enumerable.Empty<TransformationRecord>();
    }

    /// <summary>
    /// Gets all active pipelines.
    /// </summary>
    public IEnumerable<TransformationPipeline> GetActivePipelines()
    {
        return _activePipelines.Values;
    }
}

/// <summary>
/// Represents a transformation pipeline.
/// </summary>
public sealed class TransformationPipeline
{
    /// <summary>Pipeline ID.</summary>
    public required string PipelineId { get; init; }

    /// <summary>Knowledge ID being transformed.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Pipeline description.</summary>
    public string? Description { get; init; }

    /// <summary>When the pipeline started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the pipeline completed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Pipeline status.</summary>
    public PipelineStatus Status { get; set; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; set; }

    /// <summary>Steps in the pipeline.</summary>
    public List<TransformationStep> Steps { get; init; } = new();

    /// <summary>Duration of the pipeline.</summary>
    public TimeSpan? Duration => CompletedAt.HasValue
        ? CompletedAt.Value - StartedAt
        : null;
}

/// <summary>
/// Status of a transformation pipeline.
/// </summary>
public enum PipelineStatus
{
    /// <summary>Pipeline is pending.</summary>
    Pending,

    /// <summary>Pipeline is running.</summary>
    Running,

    /// <summary>Pipeline completed successfully.</summary>
    Completed,

    /// <summary>Pipeline failed.</summary>
    Failed,

    /// <summary>Pipeline was cancelled.</summary>
    Cancelled
}

/// <summary>
/// A step in a transformation pipeline.
/// </summary>
public sealed record TransformationStep
{
    /// <summary>Step ID.</summary>
    public string StepId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Step name.</summary>
    public required string Name { get; init; }

    /// <summary>Transformation type.</summary>
    public TransformationType TransformationType { get; init; }

    /// <summary>Actor performing the step.</summary>
    public required string Actor { get; init; }

    /// <summary>Actor type.</summary>
    public string ActorType { get; init; } = "system";

    /// <summary>Step description.</summary>
    public string? Description { get; init; }

    /// <summary>When the step was executed.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Step parameters.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>Step duration in milliseconds.</summary>
    public double? DurationMs { get; init; }

    /// <summary>Whether the step succeeded.</summary>
    public bool Success { get; init; } = true;
}

#endregion

#region Lineage Graph

/// <summary>
/// Visualizes and queries knowledge lineage as a graph.
/// Provides ancestry, descendant, and impact analysis.
/// </summary>
public sealed class LineageGraph
{
    private readonly BoundedDictionary<string, LineageNode> _nodes = new BoundedDictionary<string, LineageNode>(1000);
    private readonly BoundedDictionary<string, List<LineageEdge>> _outgoingEdges = new BoundedDictionary<string, List<LineageEdge>>(1000);
    private readonly BoundedDictionary<string, List<LineageEdge>> _incomingEdges = new BoundedDictionary<string, List<LineageEdge>>(1000);
    private readonly ProvenanceRecorder _recorder;

    /// <summary>
    /// Creates a new lineage graph.
    /// </summary>
    /// <param name="recorder">Provenance recorder.</param>
    public LineageGraph(ProvenanceRecorder recorder)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        BuildGraph();

        _recorder.ProvenanceCreated += OnProvenanceCreated;
    }

    /// <summary>
    /// Gets a lineage node by knowledge ID.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <returns>Lineage node, or null if not found.</returns>
    public LineageNode? GetNode(string knowledgeId)
    {
        _nodes.TryGetValue(knowledgeId, out var node);
        return node;
    }

    /// <summary>
    /// Gets all ancestors of a knowledge item.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="maxDepth">Maximum depth to traverse.</param>
    /// <returns>Ancestor nodes.</returns>
    public IEnumerable<LineageNode> GetAncestors(string knowledgeId, int maxDepth = 10)
    {
        var visited = new HashSet<string>();
        var result = new List<LineageNode>();
        var queue = new Queue<(string Id, int Depth)>();

        queue.Enqueue((knowledgeId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();

            if (depth >= maxDepth || visited.Contains(currentId))
                continue;

            visited.Add(currentId);

            if (_incomingEdges.TryGetValue(currentId, out var edges))
            {
                foreach (var edge in edges)
                {
                    if (_nodes.TryGetValue(edge.SourceId, out var node))
                    {
                        result.Add(node);
                        queue.Enqueue((edge.SourceId, depth + 1));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets all descendants of a knowledge item.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="maxDepth">Maximum depth to traverse.</param>
    /// <returns>Descendant nodes.</returns>
    public IEnumerable<LineageNode> GetDescendants(string knowledgeId, int maxDepth = 10)
    {
        var visited = new HashSet<string>();
        var result = new List<LineageNode>();
        var queue = new Queue<(string Id, int Depth)>();

        queue.Enqueue((knowledgeId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();

            if (depth >= maxDepth || visited.Contains(currentId))
                continue;

            visited.Add(currentId);

            if (_outgoingEdges.TryGetValue(currentId, out var edges))
            {
                foreach (var edge in edges)
                {
                    if (_nodes.TryGetValue(edge.TargetId, out var node))
                    {
                        result.Add(node);
                        queue.Enqueue((edge.TargetId, depth + 1));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Performs impact analysis: what would be affected if this knowledge changes.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <returns>Impact analysis result.</returns>
    public ImpactAnalysisResult AnalyzeImpact(string knowledgeId)
    {
        var descendants = GetDescendants(knowledgeId).ToList();

        var directlyAffected = descendants
            .Where(d => _incomingEdges.TryGetValue(d.KnowledgeId, out var edges) &&
                        edges.Any(e => e.SourceId == knowledgeId))
            .ToList();

        var indirectlyAffected = descendants.Except(directlyAffected).ToList();

        return new ImpactAnalysisResult
        {
            KnowledgeId = knowledgeId,
            DirectlyAffectedCount = directlyAffected.Count,
            IndirectlyAffectedCount = indirectlyAffected.Count,
            DirectlyAffected = directlyAffected,
            IndirectlyAffected = indirectlyAffected,
            TotalImpactRadius = descendants.Count > 0
                ? descendants.Max(d => GetDistance(knowledgeId, d.KnowledgeId))
                : 0
        };
    }

    /// <summary>
    /// Finds the lineage path between two knowledge items.
    /// </summary>
    /// <param name="sourceId">Source knowledge ID.</param>
    /// <param name="targetId">Target knowledge ID.</param>
    /// <param name="maxDepth">Maximum path length.</param>
    /// <returns>Path if found, or null.</returns>
    public LineagePath? FindPath(string sourceId, string targetId, int maxDepth = 20)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<(string Id, List<LineageEdge> Path)>();

        queue.Enqueue((sourceId, new List<LineageEdge>()));

        while (queue.Count > 0)
        {
            var (currentId, path) = queue.Dequeue();

            if (path.Count >= maxDepth || visited.Contains(currentId))
                continue;

            visited.Add(currentId);

            if (currentId == targetId)
            {
                return new LineagePath
                {
                    SourceId = sourceId,
                    TargetId = targetId,
                    Edges = path,
                    Length = path.Count
                };
            }

            if (_outgoingEdges.TryGetValue(currentId, out var edges))
            {
                foreach (var edge in edges)
                {
                    var newPath = new List<LineageEdge>(path) { edge };
                    queue.Enqueue((edge.TargetId, newPath));
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets graph statistics.
    /// </summary>
    public LineageGraphStatistics GetStatistics()
    {
        var totalEdges = _outgoingEdges.Values.Sum(e => e.Count);

        return new LineageGraphStatistics
        {
            TotalNodes = _nodes.Count,
            TotalEdges = totalEdges,
            RootNodes = _nodes.Values.Count(n => !_incomingEdges.ContainsKey(n.KnowledgeId)),
            LeafNodes = _nodes.Values.Count(n => !_outgoingEdges.ContainsKey(n.KnowledgeId)),
            AverageOutDegree = _nodes.Count > 0 ? (double)totalEdges / _nodes.Count : 0
        };
    }

    /// <summary>
    /// Exports the graph in DOT format for visualization.
    /// </summary>
    /// <returns>DOT format string.</returns>
    public string ExportToDot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("digraph LineageGraph {");
        sb.AppendLine("  rankdir=LR;");
        sb.AppendLine("  node [shape=box];");

        foreach (var node in _nodes.Values)
        {
            var label = $"{node.KnowledgeId}\\nTrust: {node.TrustScore:F2}";
            sb.AppendLine($"  \"{node.KnowledgeId}\" [label=\"{label}\"];");
        }

        foreach (var (sourceId, edges) in _outgoingEdges)
        {
            foreach (var edge in edges)
            {
                var label = edge.TransformationType.ToString();
                sb.AppendLine($"  \"{sourceId}\" -> \"{edge.TargetId}\" [label=\"{label}\"];");
            }
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private void BuildGraph()
    {
        foreach (var record in _recorder.GetAllRecords())
        {
            AddNodeFromProvenance(record);
        }
    }

    private void AddNodeFromProvenance(ProvenanceRecord record)
    {
        var node = new LineageNode
        {
            KnowledgeId = record.KnowledgeId,
            ProvenanceId = record.ProvenanceId,
            Origin = record.Origin,
            TrustScore = record.TrustScore,
            TrustLevel = record.TrustLevel,
            CreatedAt = record.CreatedAt
        };

        _nodes[record.KnowledgeId] = node;

        // Add edges for parent relationships
        foreach (var parentId in record.ParentProvenanceIds)
        {
            // Find knowledge ID for parent provenance
            var parentRecord = _recorder.GetAllRecords()
                .FirstOrDefault(r => r.ProvenanceId == parentId);

            if (parentRecord != null)
            {
                var edge = new LineageEdge
                {
                    SourceId = parentRecord.KnowledgeId,
                    TargetId = record.KnowledgeId,
                    TransformationType = record.Transformations.FirstOrDefault()?.Type ?? TransformationType.Derivation,
                    CreatedAt = record.CreatedAt
                };

                _outgoingEdges.GetOrAdd(parentRecord.KnowledgeId, _ => new List<LineageEdge>()).Add(edge);
                _incomingEdges.GetOrAdd(record.KnowledgeId, _ => new List<LineageEdge>()).Add(edge);
            }
        }
    }

    private void OnProvenanceCreated(object? sender, ProvenanceRecord record)
    {
        AddNodeFromProvenance(record);
    }

    private int GetDistance(string sourceId, string targetId)
    {
        var path = FindPath(sourceId, targetId);
        return path?.Length ?? 0;
    }
}

/// <summary>
/// Node in the lineage graph.
/// </summary>
public sealed record LineageNode
{
    /// <summary>Knowledge ID.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Provenance ID.</summary>
    public required string ProvenanceId { get; init; }

    /// <summary>Origin information.</summary>
    public required KnowledgeOrigin Origin { get; init; }

    /// <summary>Trust score.</summary>
    public double TrustScore { get; init; }

    /// <summary>Trust level.</summary>
    public TrustLevel TrustLevel { get; init; }

    /// <summary>When the node was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Edge in the lineage graph.
/// </summary>
public sealed record LineageEdge
{
    /// <summary>Source node ID.</summary>
    public required string SourceId { get; init; }

    /// <summary>Target node ID.</summary>
    public required string TargetId { get; init; }

    /// <summary>Transformation type that created this edge.</summary>
    public TransformationType TransformationType { get; init; }

    /// <summary>When the edge was created.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Path between two nodes in the lineage graph.
/// </summary>
public sealed record LineagePath
{
    /// <summary>Source knowledge ID.</summary>
    public required string SourceId { get; init; }

    /// <summary>Target knowledge ID.</summary>
    public required string TargetId { get; init; }

    /// <summary>Edges in the path.</summary>
    public List<LineageEdge> Edges { get; init; } = new();

    /// <summary>Path length.</summary>
    public int Length { get; init; }
}

/// <summary>
/// Result of impact analysis.
/// </summary>
public sealed record ImpactAnalysisResult
{
    /// <summary>Knowledge ID analyzed.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Number of directly affected knowledge items.</summary>
    public int DirectlyAffectedCount { get; init; }

    /// <summary>Number of indirectly affected knowledge items.</summary>
    public int IndirectlyAffectedCount { get; init; }

    /// <summary>Directly affected nodes.</summary>
    public List<LineageNode> DirectlyAffected { get; init; } = new();

    /// <summary>Indirectly affected nodes.</summary>
    public List<LineageNode> IndirectlyAffected { get; init; } = new();

    /// <summary>Maximum distance to affected nodes.</summary>
    public int TotalImpactRadius { get; init; }

    /// <summary>Total number of affected items.</summary>
    public int TotalAffected => DirectlyAffectedCount + IndirectlyAffectedCount;
}

/// <summary>
/// Statistics for the lineage graph.
/// </summary>
public sealed record LineageGraphStatistics
{
    /// <summary>Total number of nodes.</summary>
    public int TotalNodes { get; init; }

    /// <summary>Total number of edges.</summary>
    public int TotalEdges { get; init; }

    /// <summary>Number of root nodes (no parents).</summary>
    public int RootNodes { get; init; }

    /// <summary>Number of leaf nodes (no children).</summary>
    public int LeafNodes { get; init; }

    /// <summary>Average out-degree of nodes.</summary>
    public double AverageOutDegree { get; init; }
}

#endregion

#region Knowledge Signer

/// <summary>
/// Cryptographically signs knowledge for authenticity and integrity.
/// Supports ECDSA (P-256, P-384) and Ed25519 signatures.
/// </summary>
public sealed class KnowledgeSigner : IDisposable
{
    private readonly BoundedDictionary<string, SigningKey> _keys = new BoundedDictionary<string, SigningKey>(1000);
    private bool _disposed;

    /// <summary>
    /// Gets the default signing key ID.
    /// </summary>
    public string? DefaultKeyId { get; set; }

    /// <summary>
    /// Registers a signing key.
    /// </summary>
    /// <param name="keyId">Key ID.</param>
    /// <param name="algorithm">Signature algorithm.</param>
    /// <param name="privateKeyPem">Private key in PEM format.</param>
    public void RegisterKey(string keyId, SignatureAlgorithm algorithm, string privateKeyPem)
    {
        var key = new SigningKey
        {
            KeyId = keyId,
            Algorithm = algorithm,
            PrivateKeyPem = privateKeyPem,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Validate key by creating the crypto object
        switch (algorithm)
        {
            case SignatureAlgorithm.EcdsaP256Sha256:
            case SignatureAlgorithm.EcdsaP384Sha384:
                var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(privateKeyPem);
                key.CryptoKey = ecdsa;
                break;

            case SignatureAlgorithm.RsaPssSha256:
            case SignatureAlgorithm.RsaPkcs1Sha256:
                var rsa = RSA.Create();
                rsa.ImportFromPem(privateKeyPem);
                key.CryptoKey = rsa;
                break;

            case SignatureAlgorithm.Ed25519:
                // Ed25519 would use a different library in production
                throw new NotSupportedException("Ed25519 requires additional library support");
        }

        _keys[keyId] = key;

        if (DefaultKeyId == null)
        {
            DefaultKeyId = keyId;
        }
    }

    /// <summary>
    /// Signs knowledge content.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="content">Content to sign.</param>
    /// <param name="keyId">Key ID to use (or default).</param>
    /// <returns>Knowledge signature.</returns>
    public KnowledgeSignature Sign(string knowledgeId, byte[] content, string? keyId = null)
    {
        var effectiveKeyId = keyId ?? DefaultKeyId
            ?? throw new InvalidOperationException("No signing key available");

        if (!_keys.TryGetValue(effectiveKeyId, out var key))
        {
            throw new InvalidOperationException($"Key '{effectiveKeyId}' not found");
        }

        // Create canonical data to sign (hash of content + knowledge ID)
        var dataToSign = CreateSigningData(knowledgeId, content);
        var signature = SignData(dataToSign, key);

        return new KnowledgeSignature
        {
            Signature = Convert.ToBase64String(signature),
            Algorithm = key.Algorithm,
            KeyId = key.KeyId,
            SignedAt = DateTimeOffset.UtcNow,
            SignedData = Convert.ToBase64String(SHA256.HashData(dataToSign))
        };
    }

    /// <summary>
    /// Signs a provenance record.
    /// </summary>
    /// <param name="record">Provenance record to sign.</param>
    /// <param name="keyId">Key ID to use.</param>
    /// <returns>Updated provenance record with signature.</returns>
    public ProvenanceRecord SignProvenance(ProvenanceRecord record, string? keyId = null)
    {
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record with { Signature = null }));
        var signature = Sign(record.KnowledgeId, content, keyId);

        return record with { Signature = signature };
    }

    /// <summary>
    /// Gets the public key for a signing key.
    /// </summary>
    /// <param name="keyId">Key ID.</param>
    /// <returns>Public key in PEM format.</returns>
    public string GetPublicKey(string keyId)
    {
        if (!_keys.TryGetValue(keyId, out var key))
        {
            throw new InvalidOperationException($"Key '{keyId}' not found");
        }

        return key.CryptoKey switch
        {
            ECDsa ecdsa => ecdsa.ExportSubjectPublicKeyInfoPem(),
            RSA rsa => rsa.ExportSubjectPublicKeyInfoPem(),
            _ => throw new InvalidOperationException("Unknown key type")
        };
    }

    /// <summary>
    /// Gets all registered key IDs.
    /// </summary>
    public IEnumerable<string> GetKeyIds() => _keys.Keys;

    private byte[] CreateSigningData(string knowledgeId, byte[] content)
    {
        var idBytes = Encoding.UTF8.GetBytes(knowledgeId);
        var combined = new byte[idBytes.Length + content.Length];
        idBytes.CopyTo(combined, 0);
        content.CopyTo(combined, idBytes.Length);
        return combined;
    }

    private byte[] SignData(byte[] data, SigningKey key)
    {
        return key.CryptoKey switch
        {
            ECDsa ecdsa => ecdsa.SignData(data, GetHashAlgorithm(key.Algorithm)),
            RSA rsa when key.Algorithm == SignatureAlgorithm.RsaPssSha256 =>
                rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            RSA rsa => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => throw new InvalidOperationException("Unknown key type")
        };
    }

    private HashAlgorithmName GetHashAlgorithm(SignatureAlgorithm algorithm)
    {
        return algorithm switch
        {
            SignatureAlgorithm.EcdsaP256Sha256 => HashAlgorithmName.SHA256,
            SignatureAlgorithm.EcdsaP384Sha384 => HashAlgorithmName.SHA384,
            SignatureAlgorithm.RsaPssSha256 => HashAlgorithmName.SHA256,
            SignatureAlgorithm.RsaPkcs1Sha256 => HashAlgorithmName.SHA256,
            _ => HashAlgorithmName.SHA256
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var key in _keys.Values)
        {
            (key.CryptoKey as IDisposable)?.Dispose();
        }
        _keys.Clear();
    }

    private class SigningKey
    {
        public required string KeyId { get; init; }
        public SignatureAlgorithm Algorithm { get; init; }
        public required string PrivateKeyPem { get; init; }
        public object? CryptoKey { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
    }
}

#endregion

#region Signature Verifier

/// <summary>
/// Verifies cryptographic signatures on knowledge.
/// </summary>
public sealed class SignatureVerifier : IDisposable
{
    private readonly BoundedDictionary<string, VerificationKey> _publicKeys = new BoundedDictionary<string, VerificationKey>(1000);
    private bool _disposed;

    /// <summary>
    /// Registers a public key for verification.
    /// </summary>
    /// <param name="keyId">Key ID.</param>
    /// <param name="algorithm">Signature algorithm.</param>
    /// <param name="publicKeyPem">Public key in PEM format.</param>
    public void RegisterPublicKey(string keyId, SignatureAlgorithm algorithm, string publicKeyPem)
    {
        var key = new VerificationKey
        {
            KeyId = keyId,
            Algorithm = algorithm,
            PublicKeyPem = publicKeyPem
        };

        switch (algorithm)
        {
            case SignatureAlgorithm.EcdsaP256Sha256:
            case SignatureAlgorithm.EcdsaP384Sha384:
                var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(publicKeyPem);
                key.CryptoKey = ecdsa;
                break;

            case SignatureAlgorithm.RsaPssSha256:
            case SignatureAlgorithm.RsaPkcs1Sha256:
                var rsa = RSA.Create();
                rsa.ImportFromPem(publicKeyPem);
                key.CryptoKey = rsa;
                break;

            case SignatureAlgorithm.Ed25519:
                throw new NotSupportedException("Ed25519 requires additional library support");
        }

        _publicKeys[keyId] = key;
    }

    /// <summary>
    /// Verifies a knowledge signature.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="content">Content that was signed.</param>
    /// <param name="signature">Signature to verify.</param>
    /// <returns>Verification result.</returns>
    public SignatureVerificationResult Verify(
        string knowledgeId,
        byte[] content,
        KnowledgeSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (!_publicKeys.TryGetValue(signature.KeyId, out var key))
        {
            return new SignatureVerificationResult
            {
                IsValid = false,
                KeyId = signature.KeyId,
                Error = $"Public key '{signature.KeyId}' not registered"
            };
        }

        try
        {
            var dataToVerify = CreateSigningData(knowledgeId, content);
            var signatureBytes = Convert.FromBase64String(signature.Signature);

            var isValid = VerifyData(dataToVerify, signatureBytes, key);

            return new SignatureVerificationResult
            {
                IsValid = isValid,
                KeyId = signature.KeyId,
                Algorithm = signature.Algorithm,
                SignedAt = signature.SignedAt,
                VerifiedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Caught exception in ProvenanceSystem.cs: {ex.Message}");
            return new SignatureVerificationResult
            {
                IsValid = false,
                KeyId = signature.KeyId,
                Error = $"Verification failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Verifies a provenance record's signature.
    /// </summary>
    /// <param name="record">Provenance record.</param>
    /// <returns>Verification result.</returns>
    public SignatureVerificationResult VerifyProvenance(ProvenanceRecord record)
    {
        if (record.Signature == null)
        {
            return new SignatureVerificationResult
            {
                IsValid = false,
                Error = "Provenance record is not signed"
            };
        }

        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record with { Signature = null }));
        return Verify(record.KnowledgeId, content, record.Signature);
    }

    /// <summary>
    /// Gets all registered key IDs.
    /// </summary>
    public IEnumerable<string> GetKeyIds() => _publicKeys.Keys;

    private byte[] CreateSigningData(string knowledgeId, byte[] content)
    {
        var idBytes = Encoding.UTF8.GetBytes(knowledgeId);
        var combined = new byte[idBytes.Length + content.Length];
        idBytes.CopyTo(combined, 0);
        content.CopyTo(combined, idBytes.Length);
        return combined;
    }

    private bool VerifyData(byte[] data, byte[] signature, VerificationKey key)
    {
        return key.CryptoKey switch
        {
            ECDsa ecdsa => ecdsa.VerifyData(data, signature, GetHashAlgorithm(key.Algorithm)),
            RSA rsa when key.Algorithm == SignatureAlgorithm.RsaPssSha256 =>
                rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
            RSA rsa => rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => false
        };
    }

    private HashAlgorithmName GetHashAlgorithm(SignatureAlgorithm algorithm)
    {
        return algorithm switch
        {
            SignatureAlgorithm.EcdsaP256Sha256 => HashAlgorithmName.SHA256,
            SignatureAlgorithm.EcdsaP384Sha384 => HashAlgorithmName.SHA384,
            SignatureAlgorithm.RsaPssSha256 => HashAlgorithmName.SHA256,
            SignatureAlgorithm.RsaPkcs1Sha256 => HashAlgorithmName.SHA256,
            _ => HashAlgorithmName.SHA256
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var key in _publicKeys.Values)
        {
            (key.CryptoKey as IDisposable)?.Dispose();
        }
        _publicKeys.Clear();
    }

    private class VerificationKey
    {
        public required string KeyId { get; init; }
        public SignatureAlgorithm Algorithm { get; init; }
        public required string PublicKeyPem { get; init; }
        public object? CryptoKey { get; set; }
    }
}

/// <summary>
/// Result of signature verification.
/// </summary>
public sealed record SignatureVerificationResult
{
    /// <summary>Whether the signature is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Key ID used for verification.</summary>
    public string? KeyId { get; init; }

    /// <summary>Signature algorithm.</summary>
    public SignatureAlgorithm? Algorithm { get; init; }

    /// <summary>When the content was signed.</summary>
    public DateTimeOffset? SignedAt { get; init; }

    /// <summary>When verification was performed.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Error message if verification failed.</summary>
    public string? Error { get; init; }
}

#endregion

#region Trust Scorer

/// <summary>
/// Calculates trust scores for knowledge based on multiple factors.
/// </summary>
public sealed class TrustScorer
{
    private readonly SignatureVerifier _verifier;
    private readonly LineageGraph _lineageGraph;

    /// <summary>
    /// Configuration for trust scoring.
    /// </summary>
    public TrustScorerConfig Config { get; init; } = new();

    /// <summary>
    /// Creates a new trust scorer.
    /// </summary>
    /// <param name="verifier">Signature verifier.</param>
    /// <param name="lineageGraph">Lineage graph.</param>
    public TrustScorer(SignatureVerifier verifier, LineageGraph lineageGraph)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _lineageGraph = lineageGraph ?? throw new ArgumentNullException(nameof(lineageGraph));
    }

    /// <summary>
    /// Calculates the trust score for a provenance record.
    /// </summary>
    /// <param name="record">Provenance record.</param>
    /// <param name="content">Knowledge content.</param>
    /// <returns>Trust score result.</returns>
    public TrustScoreResult CalculateScore(ProvenanceRecord record, byte[] content)
    {
        var factors = new List<TrustFactor>();
        var totalWeight = 0.0;
        var weightedScore = 0.0;

        // Factor 1: Origin verification
        var originScore = CalculateOriginScore(record.Origin);
        factors.Add(new TrustFactor
        {
            Name = "Origin",
            Score = originScore,
            Weight = Config.OriginWeight,
            Details = $"Source: {record.Origin.SourceType}, Verified: {record.Origin.Verified}"
        });
        totalWeight += Config.OriginWeight;
        weightedScore += originScore * Config.OriginWeight;

        // Factor 2: Signature verification
        if (record.Signature != null)
        {
            var signatureResult = _verifier.VerifyProvenance(record);
            var signatureScore = signatureResult.IsValid ? 1.0 : 0.0;
            factors.Add(new TrustFactor
            {
                Name = "Signature",
                Score = signatureScore,
                Weight = Config.SignatureWeight,
                Details = signatureResult.IsValid ? "Valid signature" : signatureResult.Error ?? "Invalid"
            });
            totalWeight += Config.SignatureWeight;
            weightedScore += signatureScore * Config.SignatureWeight;
        }

        // Factor 3: Content integrity
        var computedHash = Convert.ToBase64String(SHA256.HashData(content));
        var integrityScore = computedHash == record.ContentHash ? 1.0 : 0.0;
        factors.Add(new TrustFactor
        {
            Name = "Integrity",
            Score = integrityScore,
            Weight = Config.IntegrityWeight,
            Details = integrityScore == 1.0 ? "Hash matches" : "Hash mismatch - tampering suspected"
        });
        totalWeight += Config.IntegrityWeight;
        weightedScore += integrityScore * Config.IntegrityWeight;

        // Factor 4: Lineage trust (trust inheritance)
        var lineageScore = CalculateLineageScore(record);
        factors.Add(new TrustFactor
        {
            Name = "Lineage",
            Score = lineageScore,
            Weight = Config.LineageWeight,
            Details = $"Based on {record.ParentProvenanceIds.Count} parent(s)"
        });
        totalWeight += Config.LineageWeight;
        weightedScore += lineageScore * Config.LineageWeight;

        // Factor 5: Age penalty (optional)
        var age = DateTimeOffset.UtcNow - record.CreatedAt;
        var ageScore = Math.Max(0, 1.0 - (age.TotalDays / Config.MaxAgeDays));
        factors.Add(new TrustFactor
        {
            Name = "Age",
            Score = ageScore,
            Weight = Config.AgeWeight,
            Details = $"Age: {age.TotalDays:F1} days"
        });
        totalWeight += Config.AgeWeight;
        weightedScore += ageScore * Config.AgeWeight;

        // Factor 6: Transformation count penalty
        var transformationScore = Math.Max(0, 1.0 - (record.Transformations.Count * Config.TransformationPenalty));
        factors.Add(new TrustFactor
        {
            Name = "Transformations",
            Score = transformationScore,
            Weight = Config.TransformationWeight,
            Details = $"{record.Transformations.Count} transformation(s)"
        });
        totalWeight += Config.TransformationWeight;
        weightedScore += transformationScore * Config.TransformationWeight;

        var finalScore = totalWeight > 0 ? weightedScore / totalWeight : 0;
        var trustLevel = finalScore >= 0.9 ? TrustLevel.Maximum
            : finalScore >= 0.7 ? TrustLevel.High
            : finalScore >= 0.5 ? TrustLevel.Medium
            : finalScore >= 0.3 ? TrustLevel.Low
            : TrustLevel.Untrusted;

        return new TrustScoreResult
        {
            KnowledgeId = record.KnowledgeId,
            Score = finalScore,
            Level = trustLevel,
            Factors = factors,
            CalculatedAt = DateTimeOffset.UtcNow
        };
    }

    private double CalculateOriginScore(KnowledgeOrigin origin)
    {
        var score = 0.5; // Base score

        if (origin.Verified)
        {
            score += 0.3;
        }

        // Trusted source types get bonus
        score += origin.SourceType.ToLowerInvariant() switch
        {
            "system" => 0.2,
            "admin" => 0.15,
            "user" => 0.1,
            "import" => 0.0,
            "federation" => 0.05,
            _ => 0.0
        };

        return Math.Min(1.0, score);
    }

    private double CalculateLineageScore(ProvenanceRecord record)
    {
        if (record.ParentProvenanceIds.Count == 0)
        {
            return 1.0; // Original content
        }

        var ancestors = _lineageGraph.GetAncestors(record.KnowledgeId, 5);
        if (!ancestors.Any())
        {
            return 0.5; // Unknown lineage
        }

        return ancestors.Average(a => a.TrustScore);
    }
}

/// <summary>
/// Configuration for trust scoring.
/// </summary>
public sealed record TrustScorerConfig
{
    /// <summary>Weight for origin verification.</summary>
    public double OriginWeight { get; init; } = 0.2;

    /// <summary>Weight for signature verification.</summary>
    public double SignatureWeight { get; init; } = 0.3;

    /// <summary>Weight for content integrity.</summary>
    public double IntegrityWeight { get; init; } = 0.25;

    /// <summary>Weight for lineage trust.</summary>
    public double LineageWeight { get; init; } = 0.15;

    /// <summary>Weight for age factor.</summary>
    public double AgeWeight { get; init; } = 0.05;

    /// <summary>Weight for transformation count.</summary>
    public double TransformationWeight { get; init; } = 0.05;

    /// <summary>Maximum age in days before trust degradation.</summary>
    public double MaxAgeDays { get; init; } = 365;

    /// <summary>Penalty per transformation.</summary>
    public double TransformationPenalty { get; init; } = 0.05;
}

/// <summary>
/// Result of trust score calculation.
/// </summary>
public sealed record TrustScoreResult
{
    /// <summary>Knowledge ID.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Calculated trust score (0.0 - 1.0).</summary>
    public double Score { get; init; }

    /// <summary>Trust level classification.</summary>
    public TrustLevel Level { get; init; }

    /// <summary>Individual trust factors.</summary>
    public List<TrustFactor> Factors { get; init; } = new();

    /// <summary>When the score was calculated.</summary>
    public DateTimeOffset CalculatedAt { get; init; }
}

/// <summary>
/// Individual factor in trust calculation.
/// </summary>
public sealed record TrustFactor
{
    /// <summary>Factor name.</summary>
    public required string Name { get; init; }

    /// <summary>Factor score (0.0 - 1.0).</summary>
    public double Score { get; init; }

    /// <summary>Factor weight.</summary>
    public double Weight { get; init; }

    /// <summary>Details about this factor.</summary>
    public string? Details { get; init; }
}

#endregion

#region Tamper Detector

/// <summary>
/// Detects tampering with knowledge content.
/// </summary>
public sealed class TamperDetector
{
    private readonly ProvenanceRecorder _recorder;
    private readonly SignatureVerifier _verifier;
    private readonly KnowledgeAuditLog _auditLog;

    /// <summary>
    /// Event raised when tampering is detected.
    /// </summary>
    public event EventHandler<TamperDetectionResult>? TamperDetected;

    /// <summary>
    /// Creates a new tamper detector.
    /// </summary>
    /// <param name="recorder">Provenance recorder.</param>
    /// <param name="verifier">Signature verifier.</param>
    /// <param name="auditLog">Audit log.</param>
    public TamperDetector(
        ProvenanceRecorder recorder,
        SignatureVerifier verifier,
        KnowledgeAuditLog auditLog)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    /// <summary>
    /// Checks knowledge for tampering.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="content">Current content.</param>
    /// <returns>Tamper detection result.</returns>
    public async Task<TamperDetectionResult> CheckAsync(
        string knowledgeId,
        byte[] content,
        CancellationToken ct = default)
    {
        var record = _recorder.GetRecord(knowledgeId);
        if (record == null)
        {
            return new TamperDetectionResult
            {
                KnowledgeId = knowledgeId,
                IsTampered = false,
                Confidence = 0,
                Details = "No provenance record found - unable to verify"
            };
        }

        var issues = new List<TamperIssue>();

        // Check 1: Content hash
        var currentHash = Convert.ToBase64String(SHA256.HashData(content));
        if (currentHash != record.ContentHash)
        {
            issues.Add(new TamperIssue
            {
                Type = TamperType.ContentModified,
                Severity = TamperSeverity.Critical,
                Description = "Content hash does not match provenance record",
                ExpectedValue = record.ContentHash,
                ActualValue = currentHash
            });
        }

        // Check 2: Signature verification
        if (record.Signature != null)
        {
            var signatureResult = _verifier.VerifyProvenance(record);
            if (!signatureResult.IsValid)
            {
                issues.Add(new TamperIssue
                {
                    Type = TamperType.SignatureInvalid,
                    Severity = TamperSeverity.Critical,
                    Description = $"Signature verification failed: {signatureResult.Error}"
                });
            }
        }

        // Check 3: Transformation chain integrity
        var chainIssue = VerifyTransformationChain(record);
        if (chainIssue != null)
        {
            issues.Add(chainIssue);
        }

        var isTampered = issues.Any(i => i.Severity == TamperSeverity.Critical);
        var confidence = CalculateConfidence(record, issues);

        var result = new TamperDetectionResult
        {
            KnowledgeId = knowledgeId,
            IsTampered = isTampered,
            Confidence = confidence,
            Issues = issues,
            CheckedAt = DateTimeOffset.UtcNow
        };

        if (isTampered)
        {
            TamperDetected?.Invoke(this, result);

            // Log to audit
            await _auditLog.RecordAsync(new AuditEntry
            {
                Action = AuditAction.TamperDetected,
                KnowledgeId = knowledgeId,
                Details = $"Tampering detected: {string.Join(", ", issues.Select(i => i.Type))}"
            }, ct);
        }

        return result;
    }

    /// <summary>
    /// Performs a batch tamper check.
    /// </summary>
    /// <param name="checks">Knowledge IDs and their content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Batch check results.</returns>
    public async Task<Dictionary<string, TamperDetectionResult>> CheckBatchAsync(
        Dictionary<string, byte[]> checks,
        CancellationToken ct = default)
    {
        var results = new BoundedDictionary<string, TamperDetectionResult>(1000);

        await Parallel.ForEachAsync(checks, ct, async (kvp, token) =>
        {
            var result = await CheckAsync(kvp.Key, kvp.Value, token);
            results[kvp.Key] = result;
        });

        return new Dictionary<string, TamperDetectionResult>(results);
    }

    private TamperIssue? VerifyTransformationChain(ProvenanceRecord record)
    {
        for (int i = 1; i < record.Transformations.Count; i++)
        {
            var prev = record.Transformations[i - 1];
            var curr = record.Transformations[i];

            // Output of previous should match input of current
            if (prev.OutputHash != null && curr.InputHash != null &&
                prev.OutputHash != curr.InputHash)
            {
                return new TamperIssue
                {
                    Type = TamperType.ChainBroken,
                    Severity = TamperSeverity.High,
                    Description = $"Transformation chain broken between step {i - 1} and {i}",
                    ExpectedValue = prev.OutputHash,
                    ActualValue = curr.InputHash
                };
            }
        }

        return null;
    }

    private double CalculateConfidence(ProvenanceRecord record, List<TamperIssue> issues)
    {
        if (issues.Count == 0)
        {
            return 1.0;
        }

        var criticalCount = issues.Count(i => i.Severity == TamperSeverity.Critical);
        var highCount = issues.Count(i => i.Severity == TamperSeverity.High);
        var lowCount = issues.Count(i => i.Severity == TamperSeverity.Low);

        // Reduce confidence based on issues
        var confidence = 1.0;
        confidence -= criticalCount * 0.3;
        confidence -= highCount * 0.15;
        confidence -= lowCount * 0.05;

        // Boost if signed
        if (record.Signature != null && !issues.Any(i => i.Type == TamperType.SignatureInvalid))
        {
            confidence = Math.Min(1.0, confidence + 0.1);
        }

        return Math.Max(0, confidence);
    }
}

/// <summary>
/// Result of tamper detection.
/// </summary>
public sealed record TamperDetectionResult
{
    /// <summary>Knowledge ID.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Whether tampering was detected.</summary>
    public bool IsTampered { get; init; }

    /// <summary>Confidence in the result (0.0 - 1.0).</summary>
    public double Confidence { get; init; }

    /// <summary>Detected issues.</summary>
    public List<TamperIssue> Issues { get; init; } = new();

    /// <summary>When the check was performed.</summary>
    public DateTimeOffset CheckedAt { get; init; }

    /// <summary>Additional details.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Individual tampering issue.
/// </summary>
public sealed record TamperIssue
{
    /// <summary>Type of tampering.</summary>
    public TamperType Type { get; init; }

    /// <summary>Severity of the issue.</summary>
    public TamperSeverity Severity { get; init; }

    /// <summary>Description of the issue.</summary>
    public required string Description { get; init; }

    /// <summary>Expected value.</summary>
    public string? ExpectedValue { get; init; }

    /// <summary>Actual value found.</summary>
    public string? ActualValue { get; init; }
}

/// <summary>
/// Type of tampering detected.
/// </summary>
public enum TamperType
{
    /// <summary>Content was modified.</summary>
    ContentModified,

    /// <summary>Signature is invalid.</summary>
    SignatureInvalid,

    /// <summary>Transformation chain is broken.</summary>
    ChainBroken,

    /// <summary>Provenance record modified.</summary>
    ProvenanceModified,

    /// <summary>Timestamp manipulation.</summary>
    TimestampManipulation
}

/// <summary>
/// Severity of tampering issue.
/// </summary>
public enum TamperSeverity
{
    /// <summary>Low severity (suspicious but not confirmed).</summary>
    Low,

    /// <summary>High severity (likely tampering).</summary>
    High,

    /// <summary>Critical (confirmed tampering).</summary>
    Critical
}

#endregion

#region Knowledge Audit Log

/// <summary>
/// Immutable audit log for knowledge operations.
/// </summary>
public sealed class KnowledgeAuditLog : IAsyncDisposable
{
    private readonly ConcurrentQueue<AuditEntry> _buffer = new();
    private readonly List<AuditEntry> _entries = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly IAuditLogStore? _store;
    private readonly Timer? _flushTimer;
    private bool _disposed;

    /// <summary>
    /// Maximum buffer size before auto-flush.
    /// </summary>
    public int MaxBufferSize { get; init; } = 1000;

    /// <summary>
    /// Flush interval in seconds.
    /// </summary>
    public int FlushIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Creates a new audit log.
    /// </summary>
    /// <param name="store">Optional persistence store.</param>
    public KnowledgeAuditLog(IAuditLogStore? store = null)
    {
        _store = store;

        _flushTimer = new Timer(
            async _ => await FlushAsync(),
            null,
            TimeSpan.FromSeconds(FlushIntervalSeconds),
            TimeSpan.FromSeconds(FlushIntervalSeconds));
    }

    /// <summary>
    /// Records an audit entry.
    /// </summary>
    /// <param name="entry">Audit entry.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordAsync(AuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var entryWithId = entry with
        {
            EntryId = entry.EntryId ?? Guid.NewGuid().ToString(),
            Timestamp = entry.Timestamp == default ? DateTimeOffset.UtcNow : entry.Timestamp
        };

        _buffer.Enqueue(entryWithId);

        if (_buffer.Count >= MaxBufferSize)
        {
            await FlushAsync(ct);
        }
    }

    /// <summary>
    /// Flushes buffered entries.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var entries = new List<AuditEntry>();
            while (_buffer.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }

            if (entries.Count == 0) return;

            _entries.AddRange(entries);

            if (_store != null)
            {
                await _store.AppendAsync(entries, ct);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Queries audit entries.
    /// </summary>
    /// <param name="query">Query parameters.</param>
    /// <returns>Matching entries.</returns>
    public IEnumerable<AuditEntry> Query(AuditQuery query)
    {
        var results = _entries.AsEnumerable();

        if (!string.IsNullOrEmpty(query.KnowledgeId))
        {
            results = results.Where(e => e.KnowledgeId == query.KnowledgeId);
        }

        if (!string.IsNullOrEmpty(query.ActorId))
        {
            results = results.Where(e => e.ActorId == query.ActorId);
        }

        if (query.Action.HasValue)
        {
            results = results.Where(e => e.Action == query.Action.Value);
        }

        if (query.After.HasValue)
        {
            results = results.Where(e => e.Timestamp >= query.After.Value);
        }

        if (query.Before.HasValue)
        {
            results = results.Where(e => e.Timestamp <= query.Before.Value);
        }

        return results.OrderByDescending(e => e.Timestamp).Take(query.MaxResults);
    }

    /// <summary>
    /// Gets audit entries for a specific knowledge ID.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="limit">Maximum entries.</param>
    /// <returns>Audit entries.</returns>
    public IEnumerable<AuditEntry> GetForKnowledge(string knowledgeId, int limit = 100)
    {
        return Query(new AuditQuery { KnowledgeId = knowledgeId, MaxResults = limit });
    }

    /// <summary>
    /// Gets audit entries for a specific actor.
    /// </summary>
    /// <param name="actorId">Actor ID.</param>
    /// <param name="limit">Maximum entries.</param>
    /// <returns>Audit entries.</returns>
    public IEnumerable<AuditEntry> GetForActor(string actorId, int limit = 100)
    {
        return Query(new AuditQuery { ActorId = actorId, MaxResults = limit });
    }

    /// <summary>
    /// Gets audit statistics.
    /// </summary>
    public AuditStatistics GetStatistics()
    {
        return new AuditStatistics
        {
            TotalEntries = _entries.Count,
            BufferedEntries = _buffer.Count,
            EntriesByAction = _entries.GroupBy(e => e.Action).ToDictionary(g => g.Key, g => g.Count()),
            OldestEntry = _entries.MinBy(e => e.Timestamp)?.Timestamp,
            NewestEntry = _entries.MaxBy(e => e.Timestamp)?.Timestamp,
            UniqueActors = _entries.Where(e => e.ActorId != null).Select(e => e.ActorId!).Distinct().Count(),
            UniqueKnowledge = _entries.Where(e => e.KnowledgeId != null).Select(e => e.KnowledgeId!).Distinct().Count()
        };
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_flushTimer != null)
        {
            await _flushTimer.DisposeAsync();
        }

        await FlushAsync();
        _writeLock.Dispose();

        if (_store is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }
}

/// <summary>
/// Audit log entry.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>Unique entry ID.</summary>
    public string? EntryId { get; init; }

    /// <summary>Action performed.</summary>
    public AuditAction Action { get; init; }

    /// <summary>Knowledge ID (if applicable).</summary>
    public string? KnowledgeId { get; init; }

    /// <summary>Actor who performed the action.</summary>
    public string? ActorId { get; init; }

    /// <summary>Actor type.</summary>
    public string? ActorType { get; init; }

    /// <summary>When the action occurred.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Action details.</summary>
    public string? Details { get; init; }

    /// <summary>Source IP address.</summary>
    public string? SourceIp { get; init; }

    /// <summary>Instance ID.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Session ID.</summary>
    public string? SessionId { get; init; }

    /// <summary>Whether the action was successful.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Error message if not successful.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Query parameters for audit log.
/// </summary>
public sealed record AuditQuery
{
    /// <summary>Filter by knowledge ID.</summary>
    public string? KnowledgeId { get; init; }

    /// <summary>Filter by actor ID.</summary>
    public string? ActorId { get; init; }

    /// <summary>Filter by action.</summary>
    public AuditAction? Action { get; init; }

    /// <summary>Filter by timestamp after.</summary>
    public DateTimeOffset? After { get; init; }

    /// <summary>Filter by timestamp before.</summary>
    public DateTimeOffset? Before { get; init; }

    /// <summary>Maximum results.</summary>
    public int MaxResults { get; init; } = 100;
}

/// <summary>
/// Statistics for the audit log.
/// </summary>
public sealed record AuditStatistics
{
    /// <summary>Total entries in log.</summary>
    public int TotalEntries { get; init; }

    /// <summary>Entries waiting to be flushed.</summary>
    public int BufferedEntries { get; init; }

    /// <summary>Entry count by action.</summary>
    public Dictionary<AuditAction, int> EntriesByAction { get; init; } = new();

    /// <summary>Oldest entry timestamp.</summary>
    public DateTimeOffset? OldestEntry { get; init; }

    /// <summary>Newest entry timestamp.</summary>
    public DateTimeOffset? NewestEntry { get; init; }

    /// <summary>Number of unique actors.</summary>
    public int UniqueActors { get; init; }

    /// <summary>Number of unique knowledge items.</summary>
    public int UniqueKnowledge { get; init; }
}

/// <summary>
/// Interface for audit log persistence.
/// </summary>
public interface IAuditLogStore
{
    /// <summary>Appends entries to the log.</summary>
    Task AppendAsync(IEnumerable<AuditEntry> entries, CancellationToken ct = default);

    /// <summary>Queries entries.</summary>
    IAsyncEnumerable<AuditEntry> QueryAsync(AuditQuery query, CancellationToken ct = default);
}

#endregion

#region Access Recorder

/// <summary>
/// Records who accessed what knowledge and when.
/// </summary>
public sealed class AccessRecorder
{
    private readonly KnowledgeAuditLog _auditLog;
    private readonly BoundedDictionary<string, List<AccessRecord>> _recentAccess = new BoundedDictionary<string, List<AccessRecord>>(1000);
    private const int MaxRecentPerKnowledge = 100;

    /// <summary>
    /// Creates a new access recorder.
    /// </summary>
    /// <param name="auditLog">Audit log.</param>
    public AccessRecorder(KnowledgeAuditLog auditLog)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    /// <summary>
    /// Records a knowledge access.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="actorId">Actor ID.</param>
    /// <param name="accessType">Type of access.</param>
    /// <param name="details">Additional details.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordAccessAsync(
        string knowledgeId,
        string actorId,
        AccessType accessType,
        string? details = null,
        CancellationToken ct = default)
    {
        var record = new AccessRecord
        {
            KnowledgeId = knowledgeId,
            ActorId = actorId,
            AccessType = accessType,
            Timestamp = DateTimeOffset.UtcNow,
            Details = details
        };

        // Store in recent access
        var recentList = _recentAccess.GetOrAdd(knowledgeId, _ => new List<AccessRecord>());
        lock (recentList)
        {
            recentList.Add(record);
            if (recentList.Count > MaxRecentPerKnowledge)
            {
                recentList.RemoveAt(0);
            }
        }

        // Log to audit
        await _auditLog.RecordAsync(new AuditEntry
        {
            Action = accessType == AccessType.Read ? AuditAction.Read : AuditAction.Query,
            KnowledgeId = knowledgeId,
            ActorId = actorId,
            Details = details
        }, ct);
    }

    /// <summary>
    /// Gets recent access for a knowledge item.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <param name="limit">Maximum records.</param>
    /// <returns>Access records.</returns>
    public IEnumerable<AccessRecord> GetRecentAccess(string knowledgeId, int limit = 50)
    {
        if (_recentAccess.TryGetValue(knowledgeId, out var records))
        {
            lock (records)
            {
                return records.TakeLast(limit).Reverse().ToList();
            }
        }
        return Enumerable.Empty<AccessRecord>();
    }

    /// <summary>
    /// Gets access patterns for a knowledge item.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <returns>Access pattern analysis.</returns>
    public AccessPatternAnalysis GetAccessPatterns(string knowledgeId)
    {
        if (!_recentAccess.TryGetValue(knowledgeId, out var records))
        {
            return new AccessPatternAnalysis { KnowledgeId = knowledgeId };
        }

        List<AccessRecord> recordsList;
        lock (records)
        {
            recordsList = records.ToList();
        }

        return new AccessPatternAnalysis
        {
            KnowledgeId = knowledgeId,
            TotalAccesses = recordsList.Count,
            UniqueActors = recordsList.Select(r => r.ActorId).Distinct().Count(),
            AccessByType = recordsList.GroupBy(r => r.AccessType).ToDictionary(g => g.Key, g => g.Count()),
            MostActiveActors = recordsList
                .GroupBy(r => r.ActorId)
                .OrderByDescending(g => g.Count())
                .Take(10)
                .ToDictionary(g => g.Key, g => g.Count()),
            FirstAccess = recordsList.MinBy(r => r.Timestamp)?.Timestamp,
            LastAccess = recordsList.MaxBy(r => r.Timestamp)?.Timestamp
        };
    }

    /// <summary>
    /// Gets access summary for an actor.
    /// </summary>
    /// <param name="actorId">Actor ID.</param>
    /// <returns>Access summary.</returns>
    public ActorAccessSummary GetActorSummary(string actorId)
    {
        var allAccess = _recentAccess.Values
            .SelectMany(records =>
            {
                lock (records)
                {
                    return records.Where(r => r.ActorId == actorId).ToList();
                }
            })
            .ToList();

        return new ActorAccessSummary
        {
            ActorId = actorId,
            TotalAccesses = allAccess.Count,
            UniqueKnowledge = allAccess.Select(r => r.KnowledgeId).Distinct().Count(),
            FirstAccess = allAccess.MinBy(r => r.Timestamp)?.Timestamp,
            LastAccess = allAccess.MaxBy(r => r.Timestamp)?.Timestamp,
            AccessByType = allAccess.GroupBy(r => r.AccessType).ToDictionary(g => g.Key, g => g.Count())
        };
    }
}

/// <summary>
/// Record of a knowledge access.
/// </summary>
public sealed record AccessRecord
{
    /// <summary>Knowledge ID.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Actor ID.</summary>
    public required string ActorId { get; init; }

    /// <summary>Type of access.</summary>
    public AccessType AccessType { get; init; }

    /// <summary>When the access occurred.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Additional details.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Type of knowledge access.
/// </summary>
public enum AccessType
{
    /// <summary>Direct read access.</summary>
    Read,

    /// <summary>Query access (search results).</summary>
    Query,

    /// <summary>Download/export access.</summary>
    Export,

    /// <summary>API access.</summary>
    Api,

    /// <summary>Federation access.</summary>
    Federation
}

/// <summary>
/// Analysis of access patterns.
/// </summary>
public sealed record AccessPatternAnalysis
{
    /// <summary>Knowledge ID.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Total access count.</summary>
    public int TotalAccesses { get; init; }

    /// <summary>Unique actors who accessed.</summary>
    public int UniqueActors { get; init; }

    /// <summary>Access count by type.</summary>
    public Dictionary<AccessType, int> AccessByType { get; init; } = new();

    /// <summary>Most active actors.</summary>
    public Dictionary<string, int> MostActiveActors { get; init; } = new();

    /// <summary>First access timestamp.</summary>
    public DateTimeOffset? FirstAccess { get; init; }

    /// <summary>Last access timestamp.</summary>
    public DateTimeOffset? LastAccess { get; init; }
}

/// <summary>
/// Summary of access for an actor.
/// </summary>
public sealed record ActorAccessSummary
{
    /// <summary>Actor ID.</summary>
    public required string ActorId { get; init; }

    /// <summary>Total access count.</summary>
    public int TotalAccesses { get; init; }

    /// <summary>Unique knowledge items accessed.</summary>
    public int UniqueKnowledge { get; init; }

    /// <summary>First access timestamp.</summary>
    public DateTimeOffset? FirstAccess { get; init; }

    /// <summary>Last access timestamp.</summary>
    public DateTimeOffset? LastAccess { get; init; }

    /// <summary>Access count by type.</summary>
    public Dictionary<AccessType, int> AccessByType { get; init; } = new();
}

#endregion

#region Compliance Reporter

/// <summary>
/// Generates compliance reports for knowledge governance.
/// </summary>
public sealed class ComplianceReporter
{
    private readonly ProvenanceRecorder _recorder;
    private readonly KnowledgeAuditLog _auditLog;
    private readonly AccessRecorder _accessRecorder;
    private readonly TamperDetector _tamperDetector;

    /// <summary>
    /// Creates a new compliance reporter.
    /// </summary>
    /// <param name="recorder">Provenance recorder.</param>
    /// <param name="auditLog">Audit log.</param>
    /// <param name="accessRecorder">Access recorder.</param>
    /// <param name="tamperDetector">Tamper detector.</param>
    public ComplianceReporter(
        ProvenanceRecorder recorder,
        KnowledgeAuditLog auditLog,
        AccessRecorder accessRecorder,
        TamperDetector tamperDetector)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _accessRecorder = accessRecorder ?? throw new ArgumentNullException(nameof(accessRecorder));
        _tamperDetector = tamperDetector ?? throw new ArgumentNullException(nameof(tamperDetector));
    }

    /// <summary>
    /// Generates a compliance report for a time period.
    /// </summary>
    /// <param name="startDate">Report start date.</param>
    /// <param name="endDate">Report end date.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Compliance report.</returns>
    public async Task<ComplianceReport> GenerateReportAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken ct = default)
    {
        var auditStats = _auditLog.GetStatistics();
        var provenanceRecords = _recorder.GetAllRecords()
            .Where(r => r.CreatedAt >= startDate && r.CreatedAt <= endDate)
            .ToList();

        var auditEntries = _auditLog.Query(new AuditQuery
        {
            After = startDate,
            Before = endDate,
            MaxResults = 10000
        }).ToList();

        // Calculate metrics
        var signedCount = provenanceRecords.Count(r => r.Signature != null);
        var verifiedOrigins = provenanceRecords.Count(r => r.Origin.Verified);
        var tamperAlerts = auditEntries.Count(e => e.Action == AuditAction.TamperDetected);
        var accessDenied = auditEntries.Count(e => e.Action == AuditAction.AccessDenied);

        return new ComplianceReport
        {
            ReportId = Guid.NewGuid().ToString(),
            GeneratedAt = DateTimeOffset.UtcNow,
            ReportPeriod = new ReportPeriod { Start = startDate, End = endDate },
            Summary = new ComplianceSummary
            {
                TotalKnowledgeItems = provenanceRecords.Count,
                SignedKnowledgeCount = signedCount,
                SignedPercentage = provenanceRecords.Count > 0
                    ? (double)signedCount / provenanceRecords.Count * 100
                    : 0,
                VerifiedOriginsCount = verifiedOrigins,
                VerifiedOriginsPercentage = provenanceRecords.Count > 0
                    ? (double)verifiedOrigins / provenanceRecords.Count * 100
                    : 0,
                TamperAlertsCount = tamperAlerts,
                AccessDeniedCount = accessDenied,
                TotalAuditEntries = auditEntries.Count
            },
            TrustDistribution = provenanceRecords
                .GroupBy(r => r.TrustLevel)
                .ToDictionary(g => g.Key, g => g.Count()),
            ActionDistribution = auditEntries
                .GroupBy(e => e.Action)
                .ToDictionary(g => g.Key, g => g.Count()),
            TopAccessedKnowledge = GetTopAccessedKnowledge(auditEntries, 10),
            SecurityIncidents = auditEntries
                .Where(e => e.Action == AuditAction.TamperDetected || e.Action == AuditAction.AccessDenied)
                .Select(e => new SecurityIncident
                {
                    Timestamp = e.Timestamp,
                    Type = e.Action == AuditAction.TamperDetected ? "Tamper Detected" : "Access Denied",
                    KnowledgeId = e.KnowledgeId,
                    ActorId = e.ActorId,
                    Details = e.Details
                })
                .ToList(),
            Recommendations = GenerateRecommendations(provenanceRecords, auditEntries)
        };
    }

    /// <summary>
    /// Generates a knowledge lineage report.
    /// </summary>
    /// <param name="knowledgeId">Knowledge ID.</param>
    /// <returns>Lineage report.</returns>
    public KnowledgeLineageReport GenerateLineageReport(string knowledgeId)
    {
        var record = _recorder.GetRecord(knowledgeId);
        if (record == null)
        {
            throw new InvalidOperationException($"Knowledge '{knowledgeId}' not found");
        }

        var accessPatterns = _accessRecorder.GetAccessPatterns(knowledgeId);
        var auditEntries = _auditLog.GetForKnowledge(knowledgeId).ToList();

        return new KnowledgeLineageReport
        {
            KnowledgeId = knowledgeId,
            GeneratedAt = DateTimeOffset.UtcNow,
            Provenance = record,
            AccessPatterns = accessPatterns,
            AuditHistory = auditEntries,
            TransformationChain = record.Transformations.Select((t, i) => new TransformationChainItem
            {
                Step = i + 1,
                Type = t.Type,
                Actor = t.Actor,
                Timestamp = t.Timestamp,
                Description = t.Description
            }).ToList()
        };
    }

    /// <summary>
    /// Exports a compliance report to JSON.
    /// </summary>
    /// <param name="report">Report to export.</param>
    /// <returns>JSON string.</returns>
    public string ExportToJson(ComplianceReport report)
    {
        return JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
    }

    private List<KnowledgeAccessSummary> GetTopAccessedKnowledge(List<AuditEntry> entries, int count)
    {
        return entries
            .Where(e => e.KnowledgeId != null && e.Action == AuditAction.Read)
            .GroupBy(e => e.KnowledgeId!)
            .Select(g => new KnowledgeAccessSummary
            {
                KnowledgeId = g.Key,
                AccessCount = g.Count(),
                UniqueActors = g.Where(e => e.ActorId != null).Select(e => e.ActorId!).Distinct().Count(),
                LastAccess = g.Max(e => e.Timestamp)
            })
            .OrderByDescending(s => s.AccessCount)
            .Take(count)
            .ToList();
    }

    private List<string> GenerateRecommendations(
        List<ProvenanceRecord> records,
        List<AuditEntry> auditEntries)
    {
        var recommendations = new List<string>();

        // Check for unsigned knowledge
        var unsignedPercentage = records.Count > 0
            ? (double)records.Count(r => r.Signature == null) / records.Count * 100
            : 0;

        if (unsignedPercentage > 20)
        {
            recommendations.Add(
                $"Consider signing more knowledge items. Currently {unsignedPercentage:F1}% are unsigned.");
        }

        // Check for unverified origins
        var unverifiedPercentage = records.Count > 0
            ? (double)records.Count(r => !r.Origin.Verified) / records.Count * 100
            : 0;

        if (unverifiedPercentage > 30)
        {
            recommendations.Add(
                $"Verify knowledge origins. Currently {unverifiedPercentage:F1}% have unverified origins.");
        }

        // Check for low trust scores
        var lowTrustCount = records.Count(r => r.TrustScore < 0.5);
        if (lowTrustCount > 0)
        {
            recommendations.Add(
                $"Review {lowTrustCount} knowledge items with low trust scores.");
        }

        // Check for tamper alerts
        var tamperCount = auditEntries.Count(e => e.Action == AuditAction.TamperDetected);
        if (tamperCount > 0)
        {
            recommendations.Add(
                $"Investigate {tamperCount} tamper alert(s) detected during the reporting period.");
        }

        return recommendations;
    }
}

/// <summary>
/// Compliance report.
/// </summary>
public sealed record ComplianceReport
{
    /// <summary>Report ID.</summary>
    public required string ReportId { get; init; }

    /// <summary>When the report was generated.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Report period.</summary>
    public required ReportPeriod ReportPeriod { get; init; }

    /// <summary>Summary metrics.</summary>
    public required ComplianceSummary Summary { get; init; }

    /// <summary>Distribution of trust levels.</summary>
    public Dictionary<TrustLevel, int> TrustDistribution { get; init; } = new();

    /// <summary>Distribution of actions.</summary>
    public Dictionary<AuditAction, int> ActionDistribution { get; init; } = new();

    /// <summary>Top accessed knowledge items.</summary>
    public List<KnowledgeAccessSummary> TopAccessedKnowledge { get; init; } = new();

    /// <summary>Security incidents.</summary>
    public List<SecurityIncident> SecurityIncidents { get; init; } = new();

    /// <summary>Recommendations for improvement.</summary>
    public List<string> Recommendations { get; init; } = new();
}

/// <summary>
/// Report time period.
/// </summary>
public sealed record ReportPeriod
{
    /// <summary>Start of period.</summary>
    public DateTimeOffset Start { get; init; }

    /// <summary>End of period.</summary>
    public DateTimeOffset End { get; init; }

    /// <summary>Duration of period.</summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Compliance summary metrics.
/// </summary>
public sealed record ComplianceSummary
{
    /// <summary>Total knowledge items.</summary>
    public int TotalKnowledgeItems { get; init; }

    /// <summary>Signed knowledge count.</summary>
    public int SignedKnowledgeCount { get; init; }

    /// <summary>Percentage of signed knowledge.</summary>
    public double SignedPercentage { get; init; }

    /// <summary>Verified origins count.</summary>
    public int VerifiedOriginsCount { get; init; }

    /// <summary>Percentage with verified origins.</summary>
    public double VerifiedOriginsPercentage { get; init; }

    /// <summary>Number of tamper alerts.</summary>
    public int TamperAlertsCount { get; init; }

    /// <summary>Number of access denied events.</summary>
    public int AccessDeniedCount { get; init; }

    /// <summary>Total audit entries.</summary>
    public int TotalAuditEntries { get; init; }
}

/// <summary>
/// Summary of knowledge access.
/// </summary>
public sealed record KnowledgeAccessSummary
{
    /// <summary>Knowledge ID.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>Total access count.</summary>
    public int AccessCount { get; init; }

    /// <summary>Unique actors who accessed.</summary>
    public int UniqueActors { get; init; }

    /// <summary>Last access time.</summary>
    public DateTimeOffset LastAccess { get; init; }
}

/// <summary>
/// Security incident record.
/// </summary>
public sealed record SecurityIncident
{
    /// <summary>When the incident occurred.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Incident type.</summary>
    public required string Type { get; init; }

    /// <summary>Affected knowledge ID.</summary>
    public string? KnowledgeId { get; init; }

    /// <summary>Actor involved.</summary>
    public string? ActorId { get; init; }

    /// <summary>Incident details.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Knowledge lineage report.
/// </summary>
public sealed record KnowledgeLineageReport
{
    /// <summary>Knowledge ID.</summary>
    public required string KnowledgeId { get; init; }

    /// <summary>When the report was generated.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Provenance record.</summary>
    public required ProvenanceRecord Provenance { get; init; }

    /// <summary>Access patterns.</summary>
    public required AccessPatternAnalysis AccessPatterns { get; init; }

    /// <summary>Audit history.</summary>
    public List<AuditEntry> AuditHistory { get; init; } = new();

    /// <summary>Transformation chain.</summary>
    public List<TransformationChainItem> TransformationChain { get; init; } = new();
}

/// <summary>
/// Item in the transformation chain.
/// </summary>
public sealed record TransformationChainItem
{
    /// <summary>Step number.</summary>
    public int Step { get; init; }

    /// <summary>Transformation type.</summary>
    public TransformationType Type { get; init; }

    /// <summary>Actor who performed it.</summary>
    public required string Actor { get; init; }

    /// <summary>When it occurred.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Description.</summary>
    public string? Description { get; init; }
}

#endregion
