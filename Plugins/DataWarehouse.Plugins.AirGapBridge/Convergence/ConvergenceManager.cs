using DataWarehouse.Plugins.AirGapBridge.Core;
using DataWarehouse.Plugins.AirGapBridge.Detection;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.AirGapBridge.Convergence;

/// <summary>
/// Manages instance convergence support for T123/T124.
/// Implements sub-tasks 79.29, 79.30, 79.31, 79.32.
/// </summary>
public sealed class ConvergenceManager
{
    private readonly BoundedDictionary<string, DetectedInstance> _detectedInstances = new BoundedDictionary<string, DetectedInstance>(1000);
    private readonly BoundedDictionary<string, ConvergenceSession> _convergenceSessions = new BoundedDictionary<string, ConvergenceSession>(1000);
    private readonly string _localInstanceId;
    private readonly int _localSchemaVersion;

    /// <summary>
    /// Event raised when an instance is detected for convergence.
    /// Implements sub-task 79.29.
    /// </summary>
    public event EventHandler<InstanceDetectedEvent>? InstanceDetectedForConvergence;

    /// <summary>
    /// Event raised when multiple instances arrive (for multi-instance convergence).
    /// </summary>
    public event EventHandler<MultiInstanceArrivalEvent>? MultiInstanceArrival;

    /// <summary>
    /// Creates a new convergence manager.
    /// </summary>
    /// <param name="localInstanceId">Local instance identifier.</param>
    /// <param name="localSchemaVersion">Local schema version.</param>
    public ConvergenceManager(string localInstanceId, int localSchemaVersion)
    {
        _localInstanceId = localInstanceId;
        _localSchemaVersion = localSchemaVersion;
    }

    #region Sub-task 79.29: Instance Detection Events

    /// <summary>
    /// Publishes an instance detected event to the message bus.
    /// Implements sub-task 79.29.
    /// </summary>
    public void OnInstanceDetected(InstanceDetectedEvent evt)
    {
        // Store detected instance
        _detectedInstances[evt.InstanceId] = new DetectedInstance
        {
            InstanceId = evt.InstanceId,
            InstanceName = evt.InstanceName,
            DevicePath = evt.DevicePath,
            Version = evt.Version,
            Metadata = evt.Metadata,
            DetectedAt = evt.DetectedAt,
            CompatibilityVerified = evt.CompatibilityVerified,
            CompatibilityIssues = evt.CompatibilityIssues
        };

        // Raise event for message bus publishing
        InstanceDetectedForConvergence?.Invoke(this, evt);

        // Check for multi-instance arrival
        CheckMultiInstanceArrival();
    }

    /// <summary>
    /// Gets all currently detected instances.
    /// </summary>
    public IReadOnlyList<DetectedInstance> GetDetectedInstances()
    {
        return _detectedInstances.Values.ToList();
    }

    #endregion

    #region Sub-task 79.30: Multi-Instance Arrival Tracking

    /// <summary>
    /// Tracks multiple arriving instances for convergence workflow.
    /// Implements sub-task 79.30.
    /// </summary>
    private void CheckMultiInstanceArrival()
    {
        var recentArrivals = _detectedInstances.Values
            .Where(i => i.DetectedAt > DateTimeOffset.UtcNow.AddMinutes(-5))
            .ToList();

        if (recentArrivals.Count >= 2)
        {
            // Multiple instances detected within 5 minutes - potential convergence scenario
            MultiInstanceArrival?.Invoke(this, new MultiInstanceArrivalEvent
            {
                Instances = recentArrivals,
                DetectedAt = DateTimeOffset.UtcNow,
                SuggestedAction = DetermineConvergenceAction(recentArrivals)
            });
        }
    }

    /// <summary>
    /// Registers an instance arrival for tracking.
    /// </summary>
    public void RegisterInstanceArrival(string instanceId, string instanceName, string devicePath, InstanceMetadata metadata)
    {
        var evt = new InstanceDetectedEvent
        {
            InstanceId = instanceId,
            InstanceName = instanceName,
            DevicePath = devicePath,
            Version = "1.0.0",
            Metadata = metadata
        };

        OnInstanceDetected(evt);
    }

    /// <summary>
    /// Gets arrival history for convergence planning.
    /// </summary>
    public IReadOnlyList<InstanceArrivalRecord> GetArrivalHistory(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;

        return _detectedInstances.Values
            .Where(i => i.DetectedAt > cutoff)
            .OrderBy(i => i.DetectedAt)
            .Select(i => new InstanceArrivalRecord
            {
                InstanceId = i.InstanceId,
                InstanceName = i.InstanceName,
                ArrivedAt = i.DetectedAt,
                SchemaVersion = i.Metadata.SchemaVersion,
                BlobCount = i.Metadata.Statistics.BlobCount,
                TotalSize = i.Metadata.Statistics.TotalSizeBytes
            })
            .ToList();
    }

    private ConvergenceAction DetermineConvergenceAction(List<DetectedInstance> instances)
    {
        // All instances have same parent - sibling merge
        var parents = instances
            .Where(i => !string.IsNullOrEmpty(i.Metadata.ParentInstanceId))
            .Select(i => i.Metadata.ParentInstanceId)
            .Distinct()
            .ToList();

        if (parents.Count == 1 && parents[0] == _localInstanceId)
        {
            return ConvergenceAction.SiblingMerge;
        }

        // Different schema versions - migration needed
        var schemaVersions = instances.Select(i => i.Metadata.SchemaVersion).Distinct().ToList();
        if (schemaVersions.Count > 1)
        {
            return ConvergenceAction.SchemaMigrationRequired;
        }

        // Check for conflicts
        var hasOverlappingData = CheckForOverlappingData(instances);
        if (hasOverlappingData)
        {
            return ConvergenceAction.ConflictResolutionRequired;
        }

        return ConvergenceAction.DirectMerge;
    }

    private bool CheckForOverlappingData(List<DetectedInstance> instances)
    {
        // Would check for overlapping blob URIs across instances
        // Simplified implementation
        return instances.Sum(i => i.Metadata.Statistics.BlobCount) > 0;
    }

    #endregion

    #region Sub-task 79.31: Instance Metadata Extraction

    /// <summary>
    /// Extracts comprehensive metadata from a detected instance.
    /// Implements sub-task 79.31.
    /// </summary>
    public async Task<InstanceMetadata> ExtractInstanceMetadataAsync(
        string devicePath,
        CancellationToken ct = default)
    {
        var instancePath = Path.Combine(devicePath, ".dw-instance");
        var metadata = new InstanceMetadata();

        // Read metadata.json if present
        var metadataPath = Path.Combine(instancePath, "metadata.json");
        if (File.Exists(metadataPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(metadataPath, ct);
                var saved = System.Text.Json.JsonSerializer.Deserialize<InstanceMetadata>(json);
                if (saved != null)
                {
                    return saved;
                }
            }
            catch
            {
                // Continue with manual extraction
            }
        }

        // Extract from database
        var dbPath = Path.Combine(instancePath, "index.litedb");
        if (File.Exists(dbPath))
        {
            using var db = new LiteDB.LiteDatabase(dbPath);

            var blobs = db.GetCollection<object>("blobs");
            var blobCount = blobs.Count();

            // Get config hash
            var configPath = Path.Combine(devicePath, ".dw-config");
            string? configHash = null;
            if (File.Exists(configPath))
            {
                var configBytes = await File.ReadAllBytesAsync(configPath, ct);
                // Note: Bus delegation not available in this context; using direct crypto
                using var sha = System.Security.Cryptography.SHA256.Create();
                configHash = Convert.ToBase64String(sha.ComputeHash(configBytes));
            }

            metadata = new InstanceMetadata
            {
                SchemaVersion = 1, // Would read from database
                Statistics = new DataStatistics
                {
                    BlobCount = blobCount,
                    TotalSizeBytes = GetBlobsSize(instancePath),
                    LatestModification = GetLatestModification(instancePath),
                    IndexEntryCount = blobCount
                },
                ConfigHash = configHash
            };
        }

        return metadata;
    }

    /// <summary>
    /// Extracts schema information from an instance.
    /// </summary>
    public async Task<SchemaInfo> ExtractSchemaInfoAsync(
        string devicePath,
        CancellationToken ct = default)
    {
        var instancePath = Path.Combine(devicePath, ".dw-instance");
        var dbPath = Path.Combine(instancePath, "index.litedb");

        if (!File.Exists(dbPath))
        {
            return new SchemaInfo { Version = 0, IsEmpty = true };
        }

        using var db = new LiteDB.LiteDatabase(dbPath);

        var collections = db.GetCollectionNames().ToList();

        return new SchemaInfo
        {
            Version = 1, // Would read from schema version table
            Collections = collections,
            IsEmpty = collections.Count == 0 || !db.GetCollection<object>("blobs").Exists(x => true),
            CreatedAt = File.GetCreationTime(dbPath)
        };
    }

    private static long GetBlobsSize(string instancePath)
    {
        var blobsPath = Path.Combine(instancePath, "blobs");
        if (!Directory.Exists(blobsPath)) return 0;

        return Directory.EnumerateFiles(blobsPath, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    private static DateTimeOffset? GetLatestModification(string instancePath)
    {
        var blobsPath = Path.Combine(instancePath, "blobs");
        if (!Directory.Exists(blobsPath)) return null;

        var latest = Directory.EnumerateFiles(blobsPath, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f).LastWriteTimeUtc)
            .DefaultIfEmpty()
            .Max();

        return latest == default ? null : new DateTimeOffset(latest, TimeSpan.Zero);
    }

    #endregion

    #region Sub-task 79.32: Compatibility Verification

    /// <summary>
    /// Verifies instance version compatibility before convergence.
    /// Implements sub-task 79.32.
    /// </summary>
    public async Task<CompatibilityResult> VerifyCompatibilityAsync(
        string instanceId,
        CancellationToken ct = default)
    {
        if (!_detectedInstances.TryGetValue(instanceId, out var instance))
        {
            return new CompatibilityResult
            {
                IsCompatible = false,
                Reason = "Instance not found"
            };
        }

        var issues = new List<CompatibilityIssue>();

        // Check schema version
        var schemaVersion = instance.Metadata.SchemaVersion;
        if (schemaVersion > _localSchemaVersion)
        {
            issues.Add(new CompatibilityIssue
            {
                Severity = IssueSeverity.Error,
                Category = "Schema",
                Description = $"Instance schema version {schemaVersion} is newer than local {_localSchemaVersion}",
                Resolution = "Upgrade local instance before convergence"
            });
        }
        else if (schemaVersion < _localSchemaVersion - 2)
        {
            issues.Add(new CompatibilityIssue
            {
                Severity = IssueSeverity.Warning,
                Category = "Schema",
                Description = $"Instance schema version {schemaVersion} is significantly older",
                Resolution = "Migration will be performed automatically"
            });
        }

        // Check plugin compatibility
        foreach (var (pluginId, version) in instance.Metadata.PluginVersions)
        {
            // Would check against local plugin registry
            // Simplified implementation
        }

        // Check config compatibility
        if (!string.IsNullOrEmpty(instance.Metadata.ConfigHash))
        {
            // Would verify config compatibility
        }

        var isCompatible = !issues.Any(i => i.Severity == IssueSeverity.Error);

        return new CompatibilityResult
        {
            IsCompatible = isCompatible,
            Issues = issues,
            CanAutoMigrate = issues.All(i => i.Severity != IssueSeverity.Error),
            MigrationSteps = isCompatible ? GetMigrationSteps(instance) : null
        };
    }

    /// <summary>
    /// Verifies compatibility for multiple instances at once.
    /// </summary>
    public async Task<MultiCompatibilityResult> VerifyMultiInstanceCompatibilityAsync(
        IEnumerable<string> instanceIds,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, CompatibilityResult>();

        foreach (var instanceId in instanceIds)
        {
            results[instanceId] = await VerifyCompatibilityAsync(instanceId, ct);
        }

        var allCompatible = results.Values.All(r => r.IsCompatible);
        var crossIssues = AnalyzeCrossInstanceIssues(results);

        return new MultiCompatibilityResult
        {
            AllCompatible = allCompatible && crossIssues.Count == 0,
            InstanceResults = results,
            CrossInstanceIssues = crossIssues,
            SuggestedOrder = DetermineMergeOrder(results)
        };
    }

    private List<CompatibilityIssue> AnalyzeCrossInstanceIssues(Dictionary<string, CompatibilityResult> results)
    {
        var issues = new List<CompatibilityIssue>();

        // Check for schema version conflicts between instances
        var schemaVersions = _detectedInstances.Values
            .Where(i => results.ContainsKey(i.InstanceId))
            .Select(i => i.Metadata.SchemaVersion)
            .Distinct()
            .ToList();

        if (schemaVersions.Count > 1)
        {
            issues.Add(new CompatibilityIssue
            {
                Severity = IssueSeverity.Warning,
                Category = "Cross-Instance",
                Description = $"Instances have different schema versions: {string.Join(", ", schemaVersions)}",
                Resolution = "Merge will be performed in schema version order"
            });
        }

        return issues;
    }

    private List<string> DetermineMergeOrder(Dictionary<string, CompatibilityResult> results)
    {
        // Order by schema version (oldest first) for safe migration
        return _detectedInstances.Values
            .Where(i => results.ContainsKey(i.InstanceId) && results[i.InstanceId].IsCompatible)
            .OrderBy(i => i.Metadata.SchemaVersion)
            .ThenBy(i => i.Metadata.Statistics.BlobCount)
            .Select(i => i.InstanceId)
            .ToList();
    }

    private List<string> GetMigrationSteps(DetectedInstance instance)
    {
        var steps = new List<string>();

        if (instance.Metadata.SchemaVersion < _localSchemaVersion)
        {
            steps.Add($"Migrate schema from v{instance.Metadata.SchemaVersion} to v{_localSchemaVersion}");
        }

        if (instance.Metadata.Statistics.BlobCount > 0)
        {
            steps.Add($"Import {instance.Metadata.Statistics.BlobCount} blobs");
        }

        steps.Add("Update index");
        steps.Add("Verify integrity");

        return steps;
    }

    #endregion

    #region Convergence Session Management

    /// <summary>
    /// Starts a convergence session.
    /// </summary>
    public ConvergenceSession StartConvergenceSession(IEnumerable<string> instanceIds)
    {
        var session = new ConvergenceSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Instances = instanceIds.ToList(),
            StartedAt = DateTimeOffset.UtcNow,
            Status = ConvergenceStatus.Preparing
        };

        _convergenceSessions[session.SessionId] = session;

        return session;
    }

    /// <summary>
    /// Gets convergence session status.
    /// </summary>
    public ConvergenceSession? GetConvergenceSession(string sessionId)
    {
        return _convergenceSessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    #endregion
}

#region Types

/// <summary>
/// Detected instance information.
/// </summary>
public sealed class DetectedInstance
{
    public required string InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public required string DevicePath { get; init; }
    public required string Version { get; init; }
    public InstanceMetadata Metadata { get; init; } = new();
    public DateTimeOffset DetectedAt { get; init; }
    public bool CompatibilityVerified { get; set; }
    public List<string> CompatibilityIssues { get; init; } = new();
}

/// <summary>
/// Event for multiple instance arrival.
/// </summary>
public sealed class MultiInstanceArrivalEvent : EventArgs
{
    public required IReadOnlyList<DetectedInstance> Instances { get; init; }
    public DateTimeOffset DetectedAt { get; init; }
    public ConvergenceAction SuggestedAction { get; init; }
}

/// <summary>
/// Instance arrival record for history.
/// </summary>
public sealed class InstanceArrivalRecord
{
    public required string InstanceId { get; init; }
    public required string InstanceName { get; init; }
    public DateTimeOffset ArrivedAt { get; init; }
    public int SchemaVersion { get; init; }
    public long BlobCount { get; init; }
    public long TotalSize { get; init; }
}

/// <summary>
/// Convergence action types.
/// </summary>
public enum ConvergenceAction
{
    DirectMerge,
    SiblingMerge,
    SchemaMigrationRequired,
    ConflictResolutionRequired,
    ManualReviewRequired
}

/// <summary>
/// Schema information.
/// </summary>
public sealed class SchemaInfo
{
    public int Version { get; init; }
    public List<string> Collections { get; init; } = new();
    public bool IsEmpty { get; init; }
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Compatibility verification result.
/// </summary>
public sealed class CompatibilityResult
{
    public bool IsCompatible { get; init; }
    public string? Reason { get; init; }
    public List<CompatibilityIssue> Issues { get; init; } = new();
    public bool CanAutoMigrate { get; init; }
    public List<string>? MigrationSteps { get; init; }
}

/// <summary>
/// Multi-instance compatibility result.
/// </summary>
public sealed class MultiCompatibilityResult
{
    public bool AllCompatible { get; init; }
    public Dictionary<string, CompatibilityResult> InstanceResults { get; init; } = new();
    public List<CompatibilityIssue> CrossInstanceIssues { get; init; } = new();
    public List<string> SuggestedOrder { get; init; } = new();
}

/// <summary>
/// Compatibility issue.
/// </summary>
public sealed class CompatibilityIssue
{
    public IssueSeverity Severity { get; init; }
    public required string Category { get; init; }
    public required string Description { get; init; }
    public string? Resolution { get; init; }
}

/// <summary>
/// Issue severity levels.
/// </summary>
public enum IssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Convergence session.
/// </summary>
public sealed class ConvergenceSession
{
    public required string SessionId { get; init; }
    public List<string> Instances { get; init; } = new();
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ConvergenceStatus Status { get; set; }
    public int ItemsProcessed { get; set; }
    public int TotalItems { get; set; }
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// Convergence status.
/// </summary>
public enum ConvergenceStatus
{
    Preparing,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

#endregion
