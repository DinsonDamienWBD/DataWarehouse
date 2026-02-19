using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.EdgeComputing;

namespace DataWarehouse.Plugins.AirGapBridge.Security;

/// <summary>
/// Classification label enforcement for air-gap transfers. Ensures data with higher
/// classification cannot be transferred to lower-classification media.
/// </summary>
public sealed class ClassificationLabelService
{
    private readonly ConcurrentDictionary<string, ClassificationLevel> _mediaClassifications = new();
    private readonly ConcurrentDictionary<string, ClassificationLevel> _dataClassifications = new();
    private readonly List<ClassificationViolation> _violations = new();

    /// <summary>
    /// Registers classification level for a media device.
    /// </summary>
    public void RegisterMediaClassification(string mediaId, ClassificationLevel level)
    {
        _mediaClassifications[mediaId] = level;
    }

    /// <summary>
    /// Registers classification level for data.
    /// </summary>
    public void RegisterDataClassification(string dataId, ClassificationLevel level)
    {
        _dataClassifications[dataId] = level;
    }

    /// <summary>
    /// Validates whether a transfer from data to media is allowed by classification policy.
    /// </summary>
    public ClassificationCheckResult ValidateTransfer(string dataId, string mediaId)
    {
        var dataLevel = _dataClassifications.TryGetValue(dataId, out var dl) ? dl : ClassificationLevel.Unclassified;
        var mediaLevel = _mediaClassifications.TryGetValue(mediaId, out var ml) ? ml : ClassificationLevel.Unclassified;

        // Data at higher classification cannot go to lower-classification media
        if (dataLevel > mediaLevel)
        {
            var violation = new ClassificationViolation
            {
                DataId = dataId,
                MediaId = mediaId,
                DataClassification = dataLevel,
                MediaClassification = mediaLevel,
                AttemptedAt = DateTimeOffset.UtcNow,
                Reason = $"Data classified as {dataLevel} cannot be transferred to {mediaLevel} media"
            };
            lock (_violations) { _violations.Add(violation); }

            return new ClassificationCheckResult
            {
                Allowed = false,
                DataClassification = dataLevel,
                MediaClassification = mediaLevel,
                Reason = violation.Reason
            };
        }

        return new ClassificationCheckResult
        {
            Allowed = true,
            DataClassification = dataLevel,
            MediaClassification = mediaLevel,
            Reason = "Classification check passed"
        };
    }

    /// <summary>
    /// Gets all classification violations.
    /// </summary>
    public IReadOnlyList<ClassificationViolation> GetViolations()
    {
        lock (_violations) { return _violations.ToList().AsReadOnly(); }
    }
}

/// <summary>
/// Software update via physical media with package signing verification,
/// rollback on failed update, and dependency resolution.
/// </summary>
public sealed class PhysicalMediaUpdateService
{
    private readonly ConcurrentDictionary<string, UpdatePackage> _availableUpdates = new();
    private readonly ConcurrentDictionary<string, InstalledVersion> _installedVersions = new();
    private readonly List<UpdateHistory> _history = new();
    private readonly RSA _verificationKey;

    public PhysicalMediaUpdateService()
    {
        _verificationKey = RSA.Create(2048);
    }

    /// <summary>
    /// Verifies a software update package signature.
    /// </summary>
    public PackageVerificationResult VerifyPackage(byte[] packageData, byte[] signature)
    {
        try
        {
            var hash = SHA256.HashData(packageData);
            var isValid = _verificationKey.VerifyHash(hash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            return new PackageVerificationResult
            {
                IsValid = isValid,
                PackageHash = Convert.ToHexString(hash).ToLowerInvariant(),
                VerifiedAt = DateTimeOffset.UtcNow
            };
        }
        catch (CryptographicException ex)
        {
            return new PackageVerificationResult
            {
                IsValid = false,
                ErrorMessage = $"Signature verification failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Resolves dependencies for an update package.
    /// </summary>
    public DependencyResolutionResult ResolveDependencies(string packageId, string targetVersion,
        string[] requiredDependencies)
    {
        var missing = new List<string>();
        var satisfied = new List<string>();

        foreach (var dep in requiredDependencies)
        {
            if (_installedVersions.ContainsKey(dep))
                satisfied.Add(dep);
            else
                missing.Add(dep);
        }

        return new DependencyResolutionResult
        {
            PackageId = packageId,
            TargetVersion = targetVersion,
            CanInstall = missing.Count == 0,
            SatisfiedDependencies = satisfied,
            MissingDependencies = missing
        };
    }

    /// <summary>
    /// Installs an update with rollback support.
    /// </summary>
    public UpdateResult InstallUpdate(string packageId, string version, byte[] packageData, byte[] signature)
    {
        // Verify signature first
        var verification = VerifyPackage(packageData, signature);
        if (!verification.IsValid)
            return new UpdateResult { Success = false, Reason = "Package signature verification failed" };

        // Record pre-update state for rollback
        var previousVersion = _installedVersions.TryGetValue(packageId, out var prev) ? prev : null;

        try
        {
            // Simulate installation
            _installedVersions[packageId] = new InstalledVersion
            {
                PackageId = packageId,
                Version = version,
                InstalledAt = DateTimeOffset.UtcNow,
                PackageHash = verification.PackageHash ?? ""
            };

            var historyEntry = new UpdateHistory
            {
                PackageId = packageId,
                FromVersion = previousVersion?.Version ?? "none",
                ToVersion = version,
                Status = "Success",
                Timestamp = DateTimeOffset.UtcNow
            };
            lock (_history) { _history.Add(historyEntry); }

            return new UpdateResult
            {
                Success = true,
                PackageId = packageId,
                InstalledVersion = version,
                PreviousVersion = previousVersion?.Version
            };
        }
        catch (Exception ex)
        {
            // Rollback
            if (previousVersion != null)
                _installedVersions[packageId] = previousVersion;
            else
                _installedVersions.TryRemove(packageId, out _);

            lock (_history)
            {
                _history.Add(new UpdateHistory
                {
                    PackageId = packageId,
                    FromVersion = previousVersion?.Version ?? "none",
                    ToVersion = version,
                    Status = $"Failed: {ex.Message} (rolled back)",
                    Timestamp = DateTimeOffset.UtcNow
                });
            }

            return new UpdateResult
            {
                Success = false,
                Reason = $"Installation failed, rolled back: {ex.Message}",
                RolledBack = true
            };
        }
    }

    /// <summary>
    /// Gets update history.
    /// </summary>
    public IReadOnlyList<UpdateHistory> GetHistory()
    {
        lock (_history) { return _history.ToList().AsReadOnly(); }
    }
}

/// <summary>
/// Tamper-evident packaging with digital seal verification and chain-of-custody
/// validation at each transfer point.
/// </summary>
public sealed class TamperEvidentService
{
    private readonly ConcurrentDictionary<string, DigitalSeal> _seals = new();
    private readonly ConcurrentDictionary<string, List<CustodyTransfer>> _custodyChains = new();

    /// <summary>
    /// Creates a digital seal for a package.
    /// </summary>
    public DigitalSeal CreateSeal(string packageId, byte[] packageData, string creatorId)
    {
        var hash = SHA256.HashData(packageData);
        var sealId = Guid.NewGuid().ToString("N");

        var seal = new DigitalSeal
        {
            SealId = sealId,
            PackageId = packageId,
            ContentHash = Convert.ToHexString(hash).ToLowerInvariant(),
            CreatedBy = creatorId,
            CreatedAt = DateTimeOffset.UtcNow,
            IsIntact = true
        };

        _seals[sealId] = seal;

        // Initialize custody chain
        _custodyChains[sealId] = new List<CustodyTransfer>
        {
            new()
            {
                SealId = sealId,
                FromEntity = "origin",
                ToEntity = creatorId,
                TransferredAt = DateTimeOffset.UtcNow,
                VerificationStatus = "Created"
            }
        };

        return seal;
    }

    /// <summary>
    /// Verifies a digital seal against current package data.
    /// </summary>
    public SealVerificationResult VerifySeal(string sealId, byte[] currentData)
    {
        if (!_seals.TryGetValue(sealId, out var seal))
            return new SealVerificationResult { IsValid = false, Reason = "Seal not found" };

        var currentHash = Convert.ToHexString(SHA256.HashData(currentData)).ToLowerInvariant();
        var isIntact = currentHash == seal.ContentHash;

        if (!isIntact)
        {
            _seals[sealId] = seal with { IsIntact = false };
        }

        return new SealVerificationResult
        {
            IsValid = isIntact,
            SealId = sealId,
            ExpectedHash = seal.ContentHash,
            ActualHash = currentHash,
            Reason = isIntact ? "Seal intact" : "TAMPER DETECTED: content hash mismatch"
        };
    }

    /// <summary>
    /// Records a custody transfer event.
    /// </summary>
    public void RecordCustodyTransfer(string sealId, string fromEntity, string toEntity,
        byte[]? currentData = null)
    {
        var verificationStatus = "Not verified";
        if (currentData != null && _seals.TryGetValue(sealId, out var seal))
        {
            var hash = Convert.ToHexString(SHA256.HashData(currentData)).ToLowerInvariant();
            verificationStatus = hash == seal.ContentHash ? "Verified intact" : "TAMPER DETECTED";
        }

        var transfer = new CustodyTransfer
        {
            SealId = sealId,
            FromEntity = fromEntity,
            ToEntity = toEntity,
            TransferredAt = DateTimeOffset.UtcNow,
            VerificationStatus = verificationStatus
        };

        var chain = _custodyChains.GetOrAdd(sealId, _ => new List<CustodyTransfer>());
        lock (chain) { chain.Add(transfer); }
    }

    /// <summary>
    /// Gets the chain of custody for a sealed package.
    /// </summary>
    public IReadOnlyList<CustodyTransfer> GetCustodyChain(string sealId)
    {
        if (!_custodyChains.TryGetValue(sealId, out var chain))
            return Array.Empty<CustodyTransfer>();
        lock (chain) { return chain.ToList().AsReadOnly(); }
    }
}

/// <summary>
/// Offline catalog sync with bidirectional merge, conflict resolution (LWW),
/// and compaction of old versions.
/// </summary>
public sealed class OfflineCatalogSyncService
{
    private readonly ConcurrentDictionary<string, CatalogEntry> _localCatalog = new();
    private readonly List<SyncConflict> _conflicts = new();
    private int _compactionThreshold = 100;

    /// <summary>
    /// Adds or updates an entry in the local catalog.
    /// </summary>
    public void Upsert(string key, string value, string nodeId)
    {
        _localCatalog[key] = new CatalogEntry
        {
            Key = key,
            Value = value,
            Version = (_localCatalog.TryGetValue(key, out var existing) ? existing.Version : 0) + 1,
            NodeId = nodeId,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsDeleted = false
        };
    }

    /// <summary>
    /// Merges a remote catalog with the local one using Last-Writer-Wins (LWW) conflict resolution.
    /// </summary>
    public CatalogMergeResult Merge(IReadOnlyList<CatalogEntry> remoteCatalog)
    {
        var merged = 0;
        var conflictsResolved = 0;
        var skipped = 0;

        foreach (var remote in remoteCatalog)
        {
            if (_localCatalog.TryGetValue(remote.Key, out var local))
            {
                if (remote.UpdatedAt > local.UpdatedAt)
                {
                    // Remote wins (LWW)
                    _localCatalog[remote.Key] = remote;
                    merged++;

                    lock (_conflicts)
                    {
                        _conflicts.Add(new SyncConflict
                        {
                            Key = remote.Key,
                            LocalVersion = local.Version,
                            RemoteVersion = remote.Version,
                            Resolution = "RemoteWins",
                            ResolvedAt = DateTimeOffset.UtcNow
                        });
                    }
                    conflictsResolved++;
                }
                else if (remote.UpdatedAt == local.UpdatedAt && remote.Version > local.Version)
                {
                    _localCatalog[remote.Key] = remote;
                    merged++;
                    conflictsResolved++;
                }
                else
                {
                    skipped++; // Local is newer
                }
            }
            else
            {
                // New entry
                _localCatalog[remote.Key] = remote;
                merged++;
            }
        }

        return new CatalogMergeResult
        {
            Success = true,
            MergedEntries = merged,
            ConflictsResolved = conflictsResolved,
            SkippedEntries = skipped,
            TotalCatalogSize = _localCatalog.Count
        };
    }

    /// <summary>
    /// Exports the local catalog for transfer.
    /// </summary>
    public IReadOnlyList<CatalogEntry> ExportCatalog() =>
        _localCatalog.Values.Where(e => !e.IsDeleted).ToList().AsReadOnly();

    /// <summary>
    /// Compacts old versions from the catalog.
    /// </summary>
    public int CompactOldVersions(DateTimeOffset olderThan)
    {
        var toRemove = _localCatalog
            .Where(kvp => kvp.Value.IsDeleted && kvp.Value.UpdatedAt < olderThan)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
            _localCatalog.TryRemove(key, out _);

        return toRemove.Count;
    }

    /// <summary>
    /// Gets catalog statistics.
    /// </summary>
    public CatalogStats GetStats() => new()
    {
        TotalEntries = _localCatalog.Count,
        ActiveEntries = _localCatalog.Values.Count(e => !e.IsDeleted),
        DeletedEntries = _localCatalog.Values.Count(e => e.IsDeleted),
        ConflictCount = _conflicts.Count
    };
}

/// <summary>
/// Transfer bandwidth estimation service. Calculates estimated transfer time
/// based on media speed, data size, and encryption overhead.
/// </summary>
public sealed class TransferBandwidthEstimator
{
    private readonly ConcurrentDictionary<string, MediaProfile> _mediaProfiles = new();
    private readonly ConcurrentDictionary<string, List<TransferMeasurement>> _measurements = new();

    public TransferBandwidthEstimator()
    {
        InitializeDefaultProfiles();
    }

    /// <summary>
    /// Estimates transfer time for a given data size and media type.
    /// </summary>
    public TransferEstimate EstimateTransferTime(string mediaType, long dataSizeBytes,
        bool encrypted = true, double compressionRatio = 1.0)
    {
        if (!_mediaProfiles.TryGetValue(mediaType, out var profile))
            return new TransferEstimate { MediaType = mediaType, ErrorMessage = $"Unknown media type: {mediaType}" };

        var effectiveDataSize = (long)(dataSizeBytes * compressionRatio);
        var encryptionOverhead = encrypted ? profile.EncryptionOverheadFactor : 1.0;
        var effectiveThroughputBytesPerSec = profile.SustainedThroughputMBps * 1024 * 1024 / encryptionOverhead;

        var transferSeconds = effectiveDataSize / effectiveThroughputBytesPerSec;
        var totalSeconds = transferSeconds + profile.SetupTimeSeconds;

        return new TransferEstimate
        {
            MediaType = mediaType,
            OriginalDataSizeBytes = dataSizeBytes,
            EffectiveDataSizeBytes = effectiveDataSize,
            EstimatedSeconds = totalSeconds,
            EstimatedDuration = TimeSpan.FromSeconds(totalSeconds),
            EffectiveThroughputMBps = effectiveDataSize / transferSeconds / 1024 / 1024,
            Encrypted = encrypted,
            CompressionRatio = compressionRatio,
            SetupTimeSeconds = profile.SetupTimeSeconds,
            Confidence = GetConfidence(mediaType)
        };
    }

    /// <summary>
    /// Records an actual transfer measurement for calibration.
    /// </summary>
    public void RecordMeasurement(string mediaType, long dataSizeBytes, double durationSeconds)
    {
        var measurement = new TransferMeasurement
        {
            MediaType = mediaType,
            DataSizeBytes = dataSizeBytes,
            DurationSeconds = durationSeconds,
            ThroughputMBps = dataSizeBytes / durationSeconds / 1024 / 1024,
            RecordedAt = DateTimeOffset.UtcNow
        };

        var measurements = _measurements.GetOrAdd(mediaType, _ => new List<TransferMeasurement>());
        lock (measurements)
        {
            measurements.Add(measurement);
            if (measurements.Count > 100) measurements.RemoveAt(0);

            // Update profile with actual measurements
            if (_mediaProfiles.TryGetValue(mediaType, out var profile))
            {
                var avgThroughput = measurements.Average(m => m.ThroughputMBps);
                _mediaProfiles[mediaType] = profile with { SustainedThroughputMBps = avgThroughput };
            }
        }
    }

    /// <summary>
    /// Registers a custom media profile.
    /// </summary>
    public void RegisterMediaProfile(string mediaType, double sustainedThroughputMBps,
        double setupTimeSeconds = 5, double encryptionOverheadFactor = 1.15)
    {
        _mediaProfiles[mediaType] = new MediaProfile
        {
            MediaType = mediaType,
            SustainedThroughputMBps = sustainedThroughputMBps,
            SetupTimeSeconds = setupTimeSeconds,
            EncryptionOverheadFactor = encryptionOverheadFactor
        };
    }

    private double GetConfidence(string mediaType)
    {
        if (!_measurements.TryGetValue(mediaType, out var measurements) || measurements.Count < 3)
            return 0.5; // Low confidence without measurements
        return Math.Min(0.95, 0.5 + measurements.Count * 0.05); // Increases with more measurements
    }

    private void InitializeDefaultProfiles()
    {
        _mediaProfiles["usb3"] = new MediaProfile { MediaType = "usb3", SustainedThroughputMBps = 350, SetupTimeSeconds = 3, EncryptionOverheadFactor = 1.15 };
        _mediaProfiles["usb2"] = new MediaProfile { MediaType = "usb2", SustainedThroughputMBps = 35, SetupTimeSeconds = 3, EncryptionOverheadFactor = 1.20 };
        _mediaProfiles["sata-ssd"] = new MediaProfile { MediaType = "sata-ssd", SustainedThroughputMBps = 500, SetupTimeSeconds = 5, EncryptionOverheadFactor = 1.10 };
        _mediaProfiles["nvme"] = new MediaProfile { MediaType = "nvme", SustainedThroughputMBps = 2000, SetupTimeSeconds = 2, EncryptionOverheadFactor = 1.08 };
        _mediaProfiles["sd-card"] = new MediaProfile { MediaType = "sd-card", SustainedThroughputMBps = 80, SetupTimeSeconds = 2, EncryptionOverheadFactor = 1.20 };
        _mediaProfiles["optical-dvd"] = new MediaProfile { MediaType = "optical-dvd", SustainedThroughputMBps = 20, SetupTimeSeconds = 15, EncryptionOverheadFactor = 1.25 };
        _mediaProfiles["optical-bd"] = new MediaProfile { MediaType = "optical-bd", SustainedThroughputMBps = 50, SetupTimeSeconds = 15, EncryptionOverheadFactor = 1.20 };
        _mediaProfiles["tape-lto8"] = new MediaProfile { MediaType = "tape-lto8", SustainedThroughputMBps = 360, SetupTimeSeconds = 60, EncryptionOverheadFactor = 1.05 };
    }
}

#region Models

public enum ClassificationLevel
{
    Unclassified = 0,
    ForOfficialUseOnly = 1,
    Confidential = 2,
    Secret = 3,
    TopSecret = 4,
    TopSecretSci = 5
}

public sealed record ClassificationCheckResult
{
    public bool Allowed { get; init; }
    public ClassificationLevel DataClassification { get; init; }
    public ClassificationLevel MediaClassification { get; init; }
    public required string Reason { get; init; }
}

public sealed record ClassificationViolation
{
    public required string DataId { get; init; }
    public required string MediaId { get; init; }
    public ClassificationLevel DataClassification { get; init; }
    public ClassificationLevel MediaClassification { get; init; }
    public DateTimeOffset AttemptedAt { get; init; }
    public required string Reason { get; init; }
}

public sealed record PackageVerificationResult
{
    public bool IsValid { get; init; }
    public string? PackageHash { get; init; }
    public DateTimeOffset VerifiedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record DependencyResolutionResult
{
    public required string PackageId { get; init; }
    public required string TargetVersion { get; init; }
    public bool CanInstall { get; init; }
    public List<string> SatisfiedDependencies { get; init; } = new();
    public List<string> MissingDependencies { get; init; } = new();
}

public sealed record UpdateResult
{
    public bool Success { get; init; }
    public string? PackageId { get; init; }
    public string? InstalledVersion { get; init; }
    public string? PreviousVersion { get; init; }
    public string? Reason { get; init; }
    public bool RolledBack { get; init; }
}

public sealed record InstalledVersion
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public DateTimeOffset InstalledAt { get; init; }
    public required string PackageHash { get; init; }
}

public sealed record UpdateHistory
{
    public required string PackageId { get; init; }
    public required string FromVersion { get; init; }
    public required string ToVersion { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record DigitalSeal
{
    public required string SealId { get; init; }
    public required string PackageId { get; init; }
    public required string ContentHash { get; init; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsIntact { get; init; }
}

public sealed record SealVerificationResult
{
    public bool IsValid { get; init; }
    public string? SealId { get; init; }
    public string? ExpectedHash { get; init; }
    public string? ActualHash { get; init; }
    public required string Reason { get; init; }
}

public sealed record CustodyTransfer
{
    public required string SealId { get; init; }
    public required string FromEntity { get; init; }
    public required string ToEntity { get; init; }
    public DateTimeOffset TransferredAt { get; init; }
    public required string VerificationStatus { get; init; }
}

public sealed record CatalogEntry
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public int Version { get; init; }
    public required string NodeId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool IsDeleted { get; init; }
}

public sealed record CatalogMergeResult
{
    public bool Success { get; init; }
    public int MergedEntries { get; init; }
    public int ConflictsResolved { get; init; }
    public int SkippedEntries { get; init; }
    public int TotalCatalogSize { get; init; }
}

public sealed record CatalogStats
{
    public int TotalEntries { get; init; }
    public int ActiveEntries { get; init; }
    public int DeletedEntries { get; init; }
    public int ConflictCount { get; init; }
}

public sealed record SyncConflict
{
    public required string Key { get; init; }
    public int LocalVersion { get; init; }
    public int RemoteVersion { get; init; }
    public required string Resolution { get; init; }
    public DateTimeOffset ResolvedAt { get; init; }
}

public sealed record MediaProfile
{
    public required string MediaType { get; init; }
    public double SustainedThroughputMBps { get; init; }
    public double SetupTimeSeconds { get; init; }
    public double EncryptionOverheadFactor { get; init; }
}

public sealed record TransferEstimate
{
    public required string MediaType { get; init; }
    public long OriginalDataSizeBytes { get; init; }
    public long EffectiveDataSizeBytes { get; init; }
    public double EstimatedSeconds { get; init; }
    public TimeSpan EstimatedDuration { get; init; }
    public double EffectiveThroughputMBps { get; init; }
    public bool Encrypted { get; init; }
    public double CompressionRatio { get; init; }
    public double SetupTimeSeconds { get; init; }
    public double Confidence { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record TransferMeasurement
{
    public required string MediaType { get; init; }
    public long DataSizeBytes { get; init; }
    public double DurationSeconds { get; init; }
    public double ThroughputMBps { get; init; }
    public DateTimeOffset RecordedAt { get; init; }
}

#endregion
