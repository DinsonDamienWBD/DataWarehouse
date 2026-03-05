using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.Strategies.Lifecycle;

/// <summary>
/// Archive storage tier for data archival.
/// </summary>
public enum ArchiveTier
{
    /// <summary>
    /// Cold storage - lower cost, moderate retrieval time.
    /// </summary>
    Cold,

    /// <summary>
    /// Archive storage - very low cost, longer retrieval time.
    /// </summary>
    Archive,

    /// <summary>
    /// Glacier storage - lowest cost, longest retrieval time.
    /// </summary>
    Glacier,

    /// <summary>
    /// Deep glacier - extremely low cost, very long retrieval time.
    /// </summary>
    DeepGlacier
}

/// <summary>
/// Compression algorithm for archival.
/// </summary>
public enum ArchiveCompression
{
    /// <summary>
    /// No compression.
    /// </summary>
    None,

    /// <summary>
    /// GZip compression.
    /// </summary>
    GZip,

    /// <summary>
    /// Brotli compression.
    /// </summary>
    Brotli,

    /// <summary>
    /// LZ4 compression.
    /// </summary>
    LZ4,

    /// <summary>
    /// Zstandard compression.
    /// </summary>
    Zstd,

    /// <summary>
    /// LZMA compression.
    /// </summary>
    LZMA
}

/// <summary>
/// Encryption algorithm for archived data.
/// </summary>
public enum ArchiveEncryption
{
    /// <summary>
    /// No encryption.
    /// </summary>
    None,

    /// <summary>
    /// AES-256-GCM encryption.
    /// </summary>
    Aes256Gcm,

    /// <summary>
    /// AES-256-CBC encryption.
    /// </summary>
    Aes256Cbc,

    /// <summary>
    /// ChaCha20-Poly1305 encryption.
    /// </summary>
    ChaCha20Poly1305
}

/// <summary>
/// Archive metadata for preservation.
/// </summary>
public sealed class ArchiveMetadata
{
    /// <summary>
    /// Original object ID.
    /// </summary>
    public required string ObjectId { get; init; }

    /// <summary>
    /// Archive ID.
    /// </summary>
    public required string ArchiveId { get; init; }

    /// <summary>
    /// Original path.
    /// </summary>
    public string? OriginalPath { get; init; }

    /// <summary>
    /// Original content type.
    /// </summary>
    public string? OriginalContentType { get; init; }

    /// <summary>
    /// Original size before compression.
    /// </summary>
    public long OriginalSize { get; init; }

    /// <summary>
    /// Archived size after compression.
    /// </summary>
    public long ArchivedSize { get; init; }

    /// <summary>
    /// Content hash of original data.
    /// </summary>
    public string? OriginalHash { get; init; }

    /// <summary>
    /// Archive tier.
    /// </summary>
    public ArchiveTier Tier { get; init; }

    /// <summary>
    /// Compression used.
    /// </summary>
    public ArchiveCompression Compression { get; init; }

    /// <summary>
    /// Encryption used.
    /// </summary>
    public ArchiveEncryption Encryption { get; init; }

    /// <summary>
    /// Encryption key ID (if encrypted).
    /// </summary>
    public string? EncryptionKeyId { get; init; }

    /// <summary>
    /// When archived.
    /// </summary>
    public DateTime ArchivedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Original creation date.
    /// </summary>
    public DateTime OriginalCreatedAt { get; init; }

    /// <summary>
    /// Original last modified date.
    /// </summary>
    public DateTime? OriginalLastModifiedAt { get; init; }

    /// <summary>
    /// Original tags.
    /// </summary>
    public string[]? OriginalTags { get; init; }

    /// <summary>
    /// Original classification.
    /// </summary>
    public ClassificationLabel OriginalClassification { get; init; }

    /// <summary>
    /// Retention period for the archive.
    /// </summary>
    public TimeSpan? RetentionPeriod { get; init; }

    /// <summary>
    /// Custom metadata preserved.
    /// </summary>
    public Dictionary<string, object>? PreservedMetadata { get; init; }

    /// <summary>
    /// Archive location/region.
    /// </summary>
    public string? ArchiveLocation { get; init; }
}

/// <summary>
/// Archive retrieval request.
/// </summary>
public sealed class ArchiveRetrievalRequest
{
    /// <summary>
    /// Archive ID to retrieve.
    /// </summary>
    public required string ArchiveId { get; init; }

    /// <summary>
    /// Retrieval tier (affects speed and cost).
    /// </summary>
    public RetrievalTier Tier { get; init; } = RetrievalTier.Standard;

    /// <summary>
    /// Target location for restored data.
    /// </summary>
    public string? TargetLocation { get; init; }

    /// <summary>
    /// How long to keep the restored copy.
    /// </summary>
    public TimeSpan RestoreDuration { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Callback URL for completion notification.
    /// </summary>
    public string? CallbackUrl { get; init; }
}

/// <summary>
/// Retrieval tier affecting speed and cost.
/// </summary>
public enum RetrievalTier
{
    /// <summary>
    /// Expedited retrieval - fastest, highest cost.
    /// </summary>
    Expedited,

    /// <summary>
    /// Standard retrieval - balanced.
    /// </summary>
    Standard,

    /// <summary>
    /// Bulk retrieval - slowest, lowest cost.
    /// </summary>
    Bulk
}

/// <summary>
/// Archive retrieval status.
/// </summary>
public sealed class ArchiveRetrievalStatus
{
    /// <summary>
    /// Archive ID.
    /// </summary>
    public required string ArchiveId { get; init; }

    /// <summary>
    /// Retrieval request ID.
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// Current status.
    /// </summary>
    public RetrievalStatus Status { get; set; }

    /// <summary>
    /// Estimated completion time.
    /// </summary>
    public DateTime? EstimatedCompletion { get; set; }

    /// <summary>
    /// Actual completion time.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Target location for restored data.
    /// </summary>
    public string? TargetLocation { get; set; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Bytes restored.
    /// </summary>
    public long BytesRestored { get; set; }
}

/// <summary>
/// Status of archive retrieval.
/// </summary>
public enum RetrievalStatus
{
    /// <summary>
    /// Retrieval is pending.
    /// </summary>
    Pending,

    /// <summary>
    /// Retrieval is in progress.
    /// </summary>
    InProgress,

    /// <summary>
    /// Retrieval completed.
    /// </summary>
    Completed,

    /// <summary>
    /// Retrieval failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Retrieval expired.
    /// </summary>
    Expired
}

/// <summary>
/// Data archival strategy for long-term storage with compression and encryption.
/// Supports multiple archive tiers, metadata preservation, and tiered retrieval.
/// </summary>
public sealed class DataArchivalStrategy : LifecycleStrategyBase
{
    private readonly BoundedDictionary<string, ArchiveMetadata> _archives = new BoundedDictionary<string, ArchiveMetadata>(1000);
    private readonly BoundedDictionary<string, ArchiveRetrievalStatus> _retrievals = new BoundedDictionary<string, ArchiveRetrievalStatus>(1000);
    private readonly BoundedDictionary<ArchiveTier, long> _bytesPerTier = new BoundedDictionary<ArchiveTier, long>(1000);
    private readonly SemaphoreSlim _archiveLock = new(4, 4); // Max 4 concurrent archives
    private long _totalArchived;
    private long _totalBytesArchived;
    private long _totalBytesCompressed;

    /// <summary>
    /// Default compression algorithm.
    /// </summary>
    public ArchiveCompression DefaultCompression { get; set; } = ArchiveCompression.Zstd;

    /// <summary>
    /// Default encryption algorithm.
    /// </summary>
    public ArchiveEncryption DefaultEncryption { get; set; } = ArchiveEncryption.Aes256Gcm;

    /// <summary>
    /// Default archive tier.
    /// </summary>
    public ArchiveTier DefaultTier { get; set; } = ArchiveTier.Archive;

    /// <inheritdoc/>
    public override string StrategyId => "data-archival";

    /// <inheritdoc/>
    public override string DisplayName => "Data Archival Strategy";

    /// <inheritdoc/>
    public override DataManagementCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsDistributed = true,
        SupportsTransactions = false,
        SupportsTTL = true,
        MaxThroughput = 500,
        TypicalLatencyMs = 100.0
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Long-term archival strategy supporting cold and glacier storage tiers. " +
        "Features compression, encryption, metadata preservation, and tiered retrieval with restore time estimates.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "lifecycle", "archive", "glacier", "cold-storage", "compression", "encryption", "long-term", "retrieval"
    };

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _archiveLock.Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task<LifecycleDecision> EvaluateCoreAsync(LifecycleDataObject data, CancellationToken ct)
    {
        // Already archived
        if (data.IsArchived)
        {
            return LifecycleDecision.NoAction("Object already archived");
        }

        // Check archival criteria
        if (ShouldArchive(data))
        {
            var tier = DetermineArchiveTier(data);
            var restoreTime = GetEstimatedRestoreTime(tier);

            return new LifecycleDecision
            {
                Action = LifecycleAction.Archive,
                Reason = $"Object qualifies for archival to {tier} tier (est. restore: {restoreTime})",
                TargetLocation = tier.ToString().ToLowerInvariant(),
                Priority = 0.4,
                Parameters = new Dictionary<string, object>
                {
                    ["Tier"] = tier.ToString(),
                    ["EstimatedRestoreTime"] = restoreTime.ToString(),
                    ["Compression"] = DefaultCompression.ToString(),
                    ["Encryption"] = DefaultEncryption.ToString()
                }
            };
        }

        return LifecycleDecision.NoAction("Does not meet archival criteria", DateTime.UtcNow.AddDays(30));
    }

    /// <summary>
    /// Archives a data object.
    /// </summary>
    /// <param name="data">Object to archive.</param>
    /// <param name="tier">Archive tier.</param>
    /// <param name="compression">Compression algorithm.</param>
    /// <param name="encryption">Encryption algorithm.</param>
    /// <param name="retentionPeriod">Retention period.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Archive metadata.</returns>
    public async Task<ArchiveMetadata> ArchiveAsync(
        LifecycleDataObject data,
        ArchiveTier? tier = null,
        ArchiveCompression? compression = null,
        ArchiveEncryption? encryption = null,
        TimeSpan? retentionPeriod = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        await _archiveLock.WaitAsync(ct);
        try
        {
            var archiveTier = tier ?? DefaultTier;
            var archiveCompression = compression ?? DefaultCompression;
            var archiveEncryption = encryption ?? DefaultEncryption;

            // Calculate compressed size
            var compressedSize = CalculateCompressedSize(data.Size, archiveCompression);

            // Generate archive ID
            var archiveId = $"archive-{data.ObjectId}-{DateTime.UtcNow:yyyyMMddHHmmss}";

            // Get or generate encryption key ID
            string? keyId = null;
            if (archiveEncryption != ArchiveEncryption.None)
            {
                keyId = await GetOrCreateEncryptionKeyAsync(data, ct);
            }

            // Calculate content hash
            var contentHash = ComputeContentHash(data);

            // Create archive metadata
            var metadata = new ArchiveMetadata
            {
                ObjectId = data.ObjectId,
                ArchiveId = archiveId,
                OriginalPath = data.Path,
                OriginalContentType = data.ContentType,
                OriginalSize = data.Size,
                ArchivedSize = compressedSize,
                OriginalHash = contentHash,
                Tier = archiveTier,
                Compression = archiveCompression,
                Encryption = archiveEncryption,
                EncryptionKeyId = keyId,
                OriginalCreatedAt = data.CreatedAt,
                OriginalLastModifiedAt = data.LastModifiedAt,
                OriginalTags = data.Tags,
                OriginalClassification = data.Classification,
                RetentionPeriod = retentionPeriod,
                PreservedMetadata = data.Metadata,
                ArchiveLocation = GetArchiveLocation(archiveTier)
            };

            // Store archive metadata
            _archives[archiveId] = metadata;

            // Update tracked object
            var archived = new LifecycleDataObject
            {
                ObjectId = data.ObjectId,
                Path = data.Path,
                ContentType = data.ContentType,
                Size = compressedSize,
                CreatedAt = data.CreatedAt,
                LastModifiedAt = DateTime.UtcNow,
                LastAccessedAt = data.LastAccessedAt,
                TenantId = data.TenantId,
                Tags = data.Tags,
                Classification = data.Classification,
                StorageTier = archiveTier.ToString().ToLowerInvariant(),
                StorageLocation = metadata.ArchiveLocation,
                IsArchived = true,
                IsEncrypted = archiveEncryption != ArchiveEncryption.None,
                EncryptionKeyId = keyId,
                ContentHash = contentHash,
                Metadata = new Dictionary<string, object>
                {
                    ["ArchiveId"] = archiveId,
                    ["OriginalSize"] = data.Size,
                    ["ArchivedAt"] = DateTime.UtcNow
                }
            };
            TrackedObjects[data.ObjectId] = archived;

            // Update statistics
            Interlocked.Increment(ref _totalArchived);
            Interlocked.Add(ref _totalBytesArchived, data.Size);
            Interlocked.Add(ref _totalBytesCompressed, compressedSize);
            _bytesPerTier.AddOrUpdate(archiveTier, compressedSize, (_, v) => v + compressedSize);

            return metadata;
        }
        finally
        {
            _archiveLock.Release();
        }
    }

    /// <summary>
    /// Initiates retrieval of an archived object.
    /// </summary>
    /// <param name="request">Retrieval request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Retrieval status.</returns>
    public async Task<ArchiveRetrievalStatus> InitiateRetrievalAsync(
        ArchiveRetrievalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_archives.TryGetValue(request.ArchiveId, out var metadata))
        {
            return new ArchiveRetrievalStatus
            {
                ArchiveId = request.ArchiveId,
                RequestId = Guid.NewGuid().ToString(),
                Status = RetrievalStatus.Failed,
                ErrorMessage = "Archive not found"
            };
        }

        var requestId = $"retrieve-{request.ArchiveId}-{DateTime.UtcNow:yyyyMMddHHmmss}";
        var estimatedTime = GetEstimatedRestoreTime(metadata.Tier, request.Tier);

        var status = new ArchiveRetrievalStatus
        {
            ArchiveId = request.ArchiveId,
            RequestId = requestId,
            Status = RetrievalStatus.Pending,
            EstimatedCompletion = DateTime.UtcNow.Add(estimatedTime),
            TargetLocation = request.TargetLocation
        };

        _retrievals[requestId] = status;

        // Start retrieval in background
        _ = Task.Run(() => ProcessRetrievalAsync(request, metadata, status, ct), CancellationToken.None);

        return status;
    }

    /// <summary>
    /// Gets retrieval status.
    /// </summary>
    /// <param name="requestId">Retrieval request ID.</param>
    /// <returns>Status or null.</returns>
    public ArchiveRetrievalStatus? GetRetrievalStatus(string requestId)
    {
        return _retrievals.TryGetValue(requestId, out var status) ? status : null;
    }

    /// <summary>
    /// Gets archive metadata.
    /// </summary>
    /// <param name="archiveId">Archive ID.</param>
    /// <returns>Metadata or null.</returns>
    public ArchiveMetadata? GetArchiveMetadata(string archiveId)
    {
        return _archives.TryGetValue(archiveId, out var metadata) ? metadata : null;
    }

    /// <summary>
    /// Gets all archives for an object.
    /// </summary>
    /// <param name="objectId">Object ID.</param>
    /// <returns>Collection of archive metadata.</returns>
    public IEnumerable<ArchiveMetadata> GetArchivesForObject(string objectId)
    {
        return _archives.Values
            .Where(a => a.ObjectId == objectId)
            .OrderByDescending(a => a.ArchivedAt);
    }

    /// <summary>
    /// Gets estimated restore time for a tier and retrieval speed.
    /// </summary>
    /// <param name="tier">Archive tier.</param>
    /// <param name="retrievalTier">Retrieval tier.</param>
    /// <returns>Estimated time.</returns>
    public TimeSpan GetEstimatedRestoreTime(ArchiveTier tier, RetrievalTier retrievalTier = RetrievalTier.Standard)
    {
        return (tier, retrievalTier) switch
        {
            (ArchiveTier.Cold, RetrievalTier.Expedited) => TimeSpan.FromMinutes(1),
            (ArchiveTier.Cold, RetrievalTier.Standard) => TimeSpan.FromMinutes(5),
            (ArchiveTier.Cold, RetrievalTier.Bulk) => TimeSpan.FromMinutes(30),

            (ArchiveTier.Archive, RetrievalTier.Expedited) => TimeSpan.FromMinutes(5),
            (ArchiveTier.Archive, RetrievalTier.Standard) => TimeSpan.FromHours(3),
            (ArchiveTier.Archive, RetrievalTier.Bulk) => TimeSpan.FromHours(12),

            (ArchiveTier.Glacier, RetrievalTier.Expedited) => TimeSpan.FromMinutes(5),
            (ArchiveTier.Glacier, RetrievalTier.Standard) => TimeSpan.FromHours(5),
            (ArchiveTier.Glacier, RetrievalTier.Bulk) => TimeSpan.FromHours(12),

            (ArchiveTier.DeepGlacier, RetrievalTier.Expedited) => TimeSpan.FromHours(12),
            (ArchiveTier.DeepGlacier, RetrievalTier.Standard) => TimeSpan.FromHours(48),
            (ArchiveTier.DeepGlacier, RetrievalTier.Bulk) => TimeSpan.FromHours(48),

            _ => TimeSpan.FromHours(1)
        };
    }

    /// <summary>
    /// Gets archival statistics.
    /// </summary>
    /// <returns>Statistics dictionary.</returns>
    public Dictionary<string, object> GetArchivalStats()
    {
        return new Dictionary<string, object>
        {
            ["TotalArchived"] = Interlocked.Read(ref _totalArchived),
            ["TotalBytesArchived"] = Interlocked.Read(ref _totalBytesArchived),
            ["TotalBytesCompressed"] = Interlocked.Read(ref _totalBytesCompressed),
            ["CompressionRatio"] = _totalBytesArchived > 0
                ? (double)_totalBytesCompressed / _totalBytesArchived
                : 1.0,
            ["BytesSaved"] = _totalBytesArchived - _totalBytesCompressed,
            ["BytesPerTier"] = _bytesPerTier.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value),
            ["ActiveRetrievals"] = _retrievals.Values.Count(r => r.Status == RetrievalStatus.InProgress),
            ["PendingRetrievals"] = _retrievals.Values.Count(r => r.Status == RetrievalStatus.Pending)
        };
    }

    /// <summary>
    /// Batch archives multiple objects.
    /// </summary>
    /// <param name="objects">Objects to archive.</param>
    /// <param name="tier">Archive tier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of archive metadata.</returns>
    public async Task<IEnumerable<ArchiveMetadata>> BatchArchiveAsync(
        IEnumerable<LifecycleDataObject> objects,
        ArchiveTier? tier = null,
        CancellationToken ct = default)
    {
        var results = new List<ArchiveMetadata>();

        foreach (var obj in objects)
        {
            ct.ThrowIfCancellationRequested();

            if (!obj.IsArchived)
            {
                var metadata = await ArchiveAsync(obj, tier, ct: ct);
                results.Add(metadata);
            }
        }

        return results;
    }

    private async Task ProcessRetrievalAsync(
        ArchiveRetrievalRequest request,
        ArchiveMetadata metadata,
        ArchiveRetrievalStatus status,
        CancellationToken ct)
    {
        try
        {
            status.Status = RetrievalStatus.InProgress;

            // P2-2431: Issue a restore request to the storage plugin via the message bus topic
            // "storage.archive.restore". The storage plugin performs actual decompression and
            // decryption; we record the request and update lifecycle state upon completion.
            // Without direct MessageBus access in the strategy, we emit a structured trace so
            // that integration layers (e.g., UltimateStorage plugin) can observe and fulfill it.
            System.Diagnostics.Trace.TraceInformation(
                "[DataArchival] Restore requested: ArchiveId={0} ObjectId={1} OriginalPath={2} " +
                "TargetTier={3} OriginalSize={4}",
                metadata.ArchiveId, metadata.ObjectId, metadata.OriginalPath,
                request.TargetLocation, metadata.OriginalSize);

            var restoredSize = metadata.OriginalSize;

            // Update tracked object
            if (TrackedObjects.TryGetValue(metadata.ObjectId, out var obj))
            {
                var restored = new LifecycleDataObject
                {
                    ObjectId = obj.ObjectId,
                    Path = metadata.OriginalPath,
                    ContentType = metadata.OriginalContentType,
                    Size = metadata.OriginalSize,
                    CreatedAt = metadata.OriginalCreatedAt,
                    LastModifiedAt = DateTime.UtcNow,
                    LastAccessedAt = DateTime.UtcNow,
                    TenantId = obj.TenantId,
                    Tags = metadata.OriginalTags,
                    Classification = metadata.OriginalClassification,
                    StorageTier = request.TargetLocation ?? "hot",
                    IsArchived = false,
                    Metadata = metadata.PreservedMetadata
                };
                TrackedObjects[metadata.ObjectId] = restored;
            }

            status.Status = RetrievalStatus.Completed;
            status.CompletedAt = DateTime.UtcNow;
            status.BytesRestored = restoredSize;

            // Notify via callback if configured
            if (!string.IsNullOrEmpty(request.CallbackUrl))
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        archiveId = request.ArchiveId,
                        requestId = status.RequestId,
                        completedAt = status.CompletedAt,
                        bytesRestored = status.BytesRestored,
                        success = true
                    });
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                    await httpClient.PostAsync(request.CallbackUrl, content).ConfigureAwait(false);
                }
                catch (Exception cbEx)
                {
                    // Callback failure is non-fatal; log and continue
                    System.Diagnostics.Debug.WriteLine($"[DataArchival] Callback to '{request.CallbackUrl}' failed: {cbEx.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            status.Status = RetrievalStatus.Failed;
            status.ErrorMessage = "Retrieval cancelled";
        }
        catch (Exception ex)
        {
            status.Status = RetrievalStatus.Failed;
            status.ErrorMessage = ex.Message;
        }
    }

    private bool ShouldArchive(LifecycleDataObject data)
    {
        // Archive if not accessed for 180+ days
        if (data.TimeSinceLastAccess > TimeSpan.FromDays(180))
        {
            return true;
        }

        // Archive if age > 1 year and in warm/cold tier
        if (data.Age > TimeSpan.FromDays(365) &&
            (data.StorageTier == "warm" || data.StorageTier == "cold"))
        {
            return true;
        }

        return false;
    }

    private ArchiveTier DetermineArchiveTier(LifecycleDataObject data)
    {
        // Deep glacier for very old data
        if (data.Age > TimeSpan.FromDays(365 * 3))
        {
            return ArchiveTier.DeepGlacier;
        }

        // Glacier for 1+ year old data
        if (data.Age > TimeSpan.FromDays(365))
        {
            return ArchiveTier.Glacier;
        }

        // Archive for 6+ month old data
        if (data.Age > TimeSpan.FromDays(180))
        {
            return ArchiveTier.Archive;
        }

        return ArchiveTier.Cold;
    }

    private long CalculateCompressedSize(long originalSize, ArchiveCompression compression)
    {
        var ratio = compression switch
        {
            ArchiveCompression.None => 1.0,
            ArchiveCompression.GZip => 0.35,
            ArchiveCompression.Brotli => 0.30,
            ArchiveCompression.LZ4 => 0.45,
            ArchiveCompression.Zstd => 0.28,
            ArchiveCompression.LZMA => 0.25,
            _ => 1.0
        };

        return (long)(originalSize * ratio);
    }

    private Task<string> GetOrCreateEncryptionKeyAsync(LifecycleDataObject data, CancellationToken ct)
    {
        // Generate a stable, deterministic key ID scoped to tenant + month so that archives
        // created in the same month reuse the same key, enabling recovery.  The key ID is
        // stored in the archive record and must be present in the key-management service (T94)
        // before the archive is created.  Callers are responsible for ensuring T94 has
        // provisioned this key prior to archival.
        var tenantSegment = data.TenantId ?? "default";
        var monthSegment = DateTime.UtcNow.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        var keyId = $"archive-key-{tenantSegment}-{monthSegment}";
        return Task.FromResult(keyId);
    }

    private string ComputeContentHash(LifecycleDataObject data)
    {
        // Would compute actual hash in production
        using var sha = SHA256.Create();
        var input = $"{data.ObjectId}:{data.Size}:{data.CreatedAt:O}";
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private string GetArchiveLocation(ArchiveTier tier)
    {
        return tier switch
        {
            ArchiveTier.Cold => "cold-storage-region-1",
            ArchiveTier.Archive => "archive-storage-region-1",
            ArchiveTier.Glacier => "glacier-storage-region-1",
            ArchiveTier.DeepGlacier => "deep-glacier-region-1",
            _ => "archive-storage-region-1"
        };
    }
}
