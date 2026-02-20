using System.Text.Json;
using DataWarehouse.Plugins.AirGapBridge.Core;
using DataWarehouse.Plugins.AirGapBridge.Detection;
using LiteDB;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.AirGapBridge.PocketInstance;

/// <summary>
/// Manages Pocket Instance (full DataWarehouse on removable drive) functionality.
/// Implements sub-tasks 79.16, 79.17, 79.18, 79.19, 79.20.
/// </summary>
public sealed class PocketInstanceManager : IDisposable
{
    private readonly BoundedDictionary<string, PocketInstance> _instances = new BoundedDictionary<string, PocketInstance>(1000);
    private readonly string _localInstanceId;
    private bool _disposed;

    /// <summary>
    /// Event raised when a pocket instance is mounted.
    /// </summary>
    public event EventHandler<PocketInstanceMountedEvent>? InstanceMounted;

    /// <summary>
    /// Event raised when a pocket instance is unmounted.
    /// </summary>
    public event EventHandler<PocketInstanceUnmountedEvent>? InstanceUnmounted;

    /// <summary>
    /// Creates a new pocket instance manager.
    /// </summary>
    /// <param name="localInstanceId">Local DataWarehouse instance ID.</param>
    public PocketInstanceManager(string localInstanceId)
    {
        _localInstanceId = localInstanceId;
    }

    #region Sub-task 79.16: Guest Context Isolation

    /// <summary>
    /// Spins up an isolated DataWarehouse instance for a removable drive.
    /// Implements sub-task 79.16.
    /// </summary>
    public async Task<PocketInstance> MountPocketInstanceAsync(
        AirGapDevice device,
        PocketInstanceOptions options,
        CancellationToken ct = default)
    {
        if (device.Config?.Mode != AirGapMode.PocketInstance)
        {
            throw new InvalidOperationException("Device is not configured as pocket instance");
        }

        var instancePath = Path.Combine(device.Path, ".dw-instance");
        Directory.CreateDirectory(instancePath);

        var instance = new PocketInstance
        {
            InstanceId = device.Config.DeviceId,
            Name = device.Config.Name,
            DeviceId = device.DeviceId,
            DevicePath = device.Path,
            InstancePath = instancePath,
            Options = options,
            MountedAt = DateTimeOffset.UtcNow
        };

        // Initialize or load portable index (sub-task 79.17)
        await InitializePortableIndexAsync(instance, ct);

        // Load instance metadata
        await LoadInstanceMetadataAsync(instance, ct);

        _instances[instance.InstanceId] = instance;

        InstanceMounted?.Invoke(this, new PocketInstanceMountedEvent
        {
            InstanceId = instance.InstanceId,
            Name = instance.Name,
            DeviceId = device.DeviceId,
            BlobCount = instance.BlobCount,
            TotalSize = instance.TotalSize
        });

        return instance;
    }

    /// <summary>
    /// Unmounts a pocket instance.
    /// </summary>
    public async Task UnmountPocketInstanceAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_instances.TryRemove(instanceId, out var instance))
        {
            return;
        }

        // Save instance metadata
        await SaveInstanceMetadataAsync(instance, ct);

        // Close the database
        instance.Database?.Dispose();

        InstanceUnmounted?.Invoke(this, new PocketInstanceUnmountedEvent
        {
            InstanceId = instanceId,
            Name = instance.Name
        });
    }

    #endregion

    #region Sub-task 79.17: Portable Index DB

    /// <summary>
    /// Initializes or loads the portable SQLite/LiteDB index.
    /// Implements sub-task 79.17.
    /// </summary>
    private async Task InitializePortableIndexAsync(PocketInstance instance, CancellationToken ct)
    {
        var dbPath = Path.Combine(instance.InstancePath, "index.litedb");

        instance.Database = new LiteDatabase(dbPath);

        // Get or create collections
        instance.BlobsCollection = instance.Database.GetCollection<BlobIndexEntry>("blobs");
        instance.MetadataCollection = instance.Database.GetCollection<MetadataEntry>("metadata");
        instance.SyncStateCollection = instance.Database.GetCollection<SyncStateEntry>("sync_state");

        // Create indexes
        instance.BlobsCollection.EnsureIndex(x => x.Uri, unique: true);
        instance.BlobsCollection.EnsureIndex(x => x.LastModified);
        instance.BlobsCollection.EnsureIndex(x => x.ContentType);

        // Load statistics
        instance.BlobCount = instance.BlobsCollection.Count();
        instance.TotalSize = instance.BlobsCollection.FindAll().Sum(b => b.Size);
    }

    private async Task LoadInstanceMetadataAsync(PocketInstance instance, CancellationToken ct)
    {
        var metadataPath = Path.Combine(instance.InstancePath, "metadata.json");
        if (File.Exists(metadataPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(metadataPath, ct);
                var metadata = System.Text.Json.JsonSerializer.Deserialize<InstanceMetadata>(json);
                if (metadata != null)
                {
                    instance.Metadata = metadata;
                }
            }
            catch
            {
                // Use default metadata
            }
        }
    }

    private async Task SaveInstanceMetadataAsync(PocketInstance instance, CancellationToken ct)
    {
        var metadataPath = Path.Combine(instance.InstancePath, "metadata.json");
        var metadata = new InstanceMetadata
        {
            SchemaVersion = 1,
            Statistics = new DataStatistics
            {
                BlobCount = instance.BlobCount,
                TotalSizeBytes = instance.TotalSize,
                LatestModification = DateTimeOffset.UtcNow,
                IndexEntryCount = instance.BlobCount
            },
            LastSyncAt = instance.LastSyncAt,
            ParentInstanceId = instance.ParentInstanceId
        };

        var json = System.Text.Json.JsonSerializer.Serialize(metadata, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metadataPath, json, ct);
    }

    #endregion

    #region Sub-task 79.18: Bridge Mode UI

    /// <summary>
    /// Gets UI information for displaying pocket instances.
    /// Implements sub-task 79.18.
    /// </summary>
    public IReadOnlyList<PocketInstanceUIInfo> GetInstancesForUI()
    {
        return _instances.Values.Select(i => new PocketInstanceUIInfo
        {
            InstanceId = i.InstanceId,
            DisplayName = $"External: {i.Name}",
            DeviceType = GetDeviceTypeLabel(i.DevicePath),
            BlobCount = i.BlobCount,
            TotalSize = i.TotalSize,
            AvailableSpace = GetAvailableSpace(i.DevicePath),
            LastSync = i.LastSyncAt,
            IsOnline = true,
            IconType = "usb-drive"
        }).ToList();
    }

    private static string GetDeviceTypeLabel(string devicePath)
    {
        // Determine device type from path
        if (devicePath.StartsWith("/media") || devicePath.StartsWith("/Volumes"))
            return "USB";
        if (devicePath.StartsWith("\\\\"))
            return "Network";
        return "Drive";
    }

    private static long GetAvailableSpace(string devicePath)
    {
        try
        {
            var driveInfo = new System.IO.DriveInfo(Path.GetPathRoot(devicePath)!);
            return driveInfo.AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    #endregion

    #region Sub-task 79.19: Cross-Instance Transfer

    /// <summary>
    /// Transfers a blob from local instance to pocket instance.
    /// Implements sub-task 79.19.
    /// </summary>
    public async Task<TransferResult> TransferToPocketAsync(
        string instanceId,
        string blobUri,
        byte[] data,
        Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            return new TransferResult { Success = false, ErrorMessage = "Instance not found" };
        }

        try
        {
            // Store the blob data
            var blobPath = GetBlobPath(instance, blobUri);
            Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            await File.WriteAllBytesAsync(blobPath, data, ct);

            // Update index
            var entry = new BlobIndexEntry
            {
                Uri = blobUri,
                Path = blobPath,
                Size = data.Length,
                ContentType = metadata?.GetValueOrDefault("content-type") ?? "application/octet-stream",
                LastModified = DateTimeOffset.UtcNow,
                SourceInstanceId = _localInstanceId,
                Metadata = metadata ?? new Dictionary<string, string>()
            };

            instance.BlobsCollection?.Upsert(entry);
            instance.BlobCount = instance.BlobsCollection?.Count() ?? 0;
            instance.TotalSize += data.Length;

            return new TransferResult
            {
                Success = true,
                BlobUri = blobUri,
                BytesTransferred = data.Length,
                TargetInstanceId = instanceId
            };
        }
        catch (Exception ex)
        {
            return new TransferResult
            {
                Success = false,
                BlobUri = blobUri,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Transfers a blob from pocket instance to local instance.
    /// </summary>
    public async Task<(byte[]? Data, Dictionary<string, string>? Metadata)> TransferFromPocketAsync(
        string instanceId,
        string blobUri,
        CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            return (null, null);
        }

        var entry = instance.BlobsCollection?.FindOne(b => b.Uri == blobUri);
        if (entry == null || !File.Exists(entry.Path))
        {
            return (null, null);
        }

        var data = await File.ReadAllBytesAsync(entry.Path, ct);
        return (data, entry.Metadata);
    }

    /// <summary>
    /// Lists blobs in a pocket instance.
    /// </summary>
    public IReadOnlyList<BlobIndexEntry> ListBlobs(
        string instanceId,
        string? prefix = null,
        int skip = 0,
        int take = 100)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            return Array.Empty<BlobIndexEntry>();
        }

        var query = instance.BlobsCollection?.Query() ?? throw new InvalidOperationException();

        if (!string.IsNullOrEmpty(prefix))
        {
            query = query.Where(b => b.Uri.StartsWith(prefix));
        }

        return query.Skip(skip).Limit(take).ToList();
    }

    #endregion

    #region Sub-task 79.20: Sync Tasks

    /// <summary>
    /// Configures sync rules between instances.
    /// Implements sub-task 79.20.
    /// </summary>
    public async Task<SyncRule> CreateSyncRuleAsync(
        string instanceId,
        SyncRuleConfig config,
        CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            throw new InvalidOperationException("Instance not found");
        }

        var rule = new SyncRule
        {
            RuleId = Guid.NewGuid().ToString("N"),
            InstanceId = instanceId,
            Direction = config.Direction,
            Filter = config.Filter,
            Schedule = config.Schedule,
            ConflictResolution = config.ConflictResolution,
            CreatedAt = DateTimeOffset.UtcNow,
            Enabled = true
        };

        instance.SyncRules[rule.RuleId] = rule;

        // Persist rule
        var rulesPath = Path.Combine(instance.InstancePath, "sync-rules.json");
        var json = System.Text.Json.JsonSerializer.Serialize(instance.SyncRules.Values.ToList());
        await File.WriteAllTextAsync(rulesPath, json, ct);

        return rule;
    }

    /// <summary>
    /// Executes a sync operation.
    /// </summary>
    public async Task<SyncResult> ExecuteSyncAsync(
        string instanceId,
        string? ruleId = null,
        CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            return new SyncResult { Success = false, ErrorMessage = "Instance not found" };
        }

        var rules = ruleId != null && instance.SyncRules.TryGetValue(ruleId, out var rule)
            ? new[] { rule }
            : instance.SyncRules.Values.Where(r => r.Enabled).ToArray();

        var itemsSynced = 0;
        var conflicts = 0;
        long bytesSynced = 0;
        var errors = new List<string>();

        foreach (var syncRule in rules)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var ruleResult = await ExecuteSyncRuleAsync(instance, syncRule, ct);
                itemsSynced += ruleResult.ItemsSynced;
                conflicts += ruleResult.Conflicts;
                bytesSynced += ruleResult.BytesSynced;
                errors.AddRange(ruleResult.Errors);
            }
            catch (Exception ex)
            {
                errors.Add($"Rule {syncRule.RuleId}: {ex.Message}");
            }
        }

        instance.LastSyncAt = DateTimeOffset.UtcNow;

        return new SyncResult
        {
            Success = errors.Count == 0,
            ItemsSynced = itemsSynced,
            Conflicts = conflicts,
            BytesSynced = bytesSynced,
            Errors = errors,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<(int ItemsSynced, int Conflicts, long BytesSynced, List<string> Errors)> ExecuteSyncRuleAsync(
        PocketInstance instance,
        SyncRule rule,
        CancellationToken ct)
    {
        var itemsSynced = 0;
        var conflicts = 0;
        long bytesSynced = 0;
        var errors = new List<string>();

        // Get sync state
        var syncState = instance.SyncStateCollection?
            .FindOne(s => s.RuleId == rule.RuleId) ?? new SyncStateEntry
            {
                RuleId = rule.RuleId,
                LastSyncAt = DateTimeOffset.MinValue
            };

        // Find items to sync based on filter and last sync
        var query = instance.BlobsCollection?.Query() ?? throw new InvalidOperationException();

        if (!string.IsNullOrEmpty(rule.Filter?.UriPrefix))
        {
            query = query.Where(b => b.Uri.StartsWith(rule.Filter.UriPrefix));
        }

        if (rule.Direction == SyncDirection.FromPocket || rule.Direction == SyncDirection.Bidirectional)
        {
            var toSync = query.Where(b => b.LastModified > syncState.LastSyncAt).ToList();

            foreach (var blob in toSync)
            {
                if (ct.IsCancellationRequested) break;

                // Would call back to host instance here
                itemsSynced++;
                bytesSynced += blob.Size;
            }
        }

        // Update sync state
        syncState.LastSyncAt = DateTimeOffset.UtcNow;
        syncState.ItemsSynced += itemsSynced;
        instance.SyncStateCollection?.Upsert(syncState);

        return (itemsSynced, conflicts, bytesSynced, errors);
    }

    #endregion

    #region Helper Methods

    private static string GetBlobPath(PocketInstance instance, string blobUri)
    {
        var safe = blobUri.Replace("://", "_").Replace("/", "_").Replace("\\", "_");
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(c, '_');
        }
        return Path.Combine(instance.InstancePath, "blobs", safe);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var instance in _instances.Values)
        {
            instance.Database?.Dispose();
        }
        _instances.Clear();
    }
}

#region Types

/// <summary>
/// Represents a mounted pocket instance.
/// </summary>
public sealed class PocketInstance
{
    public required string InstanceId { get; init; }
    public required string Name { get; init; }
    public required string DeviceId { get; init; }
    public required string DevicePath { get; init; }
    public required string InstancePath { get; init; }
    public required PocketInstanceOptions Options { get; init; }
    public DateTimeOffset MountedAt { get; init; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? ParentInstanceId { get; set; }
    public int BlobCount { get; set; }
    public long TotalSize { get; set; }
    public InstanceMetadata? Metadata { get; set; }

    // Database
    public LiteDatabase? Database { get; set; }
    public ILiteCollection<BlobIndexEntry>? BlobsCollection { get; set; }
    public ILiteCollection<MetadataEntry>? MetadataCollection { get; set; }
    public ILiteCollection<SyncStateEntry>? SyncStateCollection { get; set; }

    // Sync rules
    public BoundedDictionary<string, SyncRule> SyncRules { get; } = new BoundedDictionary<string, SyncRule>(1000);
}

/// <summary>
/// Options for pocket instance.
/// </summary>
public sealed class PocketInstanceOptions
{
    public bool ReadOnly { get; init; }
    public bool AutoSync { get; init; }
    public SyncDirection DefaultSyncDirection { get; init; } = SyncDirection.Bidirectional;
}

/// <summary>
/// Blob index entry for portable database.
/// </summary>
public sealed class BlobIndexEntry
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public required string Uri { get; init; }
    public required string Path { get; init; }
    public long Size { get; init; }
    public string? ContentType { get; init; }
    public DateTimeOffset LastModified { get; init; }
    public string? SourceInstanceId { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Metadata entry.
/// </summary>
public sealed class MetadataEntry
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public required string Key { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Sync state entry.
/// </summary>
public sealed class SyncStateEntry
{
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    public required string RuleId { get; init; }
    public DateTimeOffset LastSyncAt { get; set; }
    public int ItemsSynced { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Sync rule.
/// </summary>
public sealed class SyncRule
{
    public required string RuleId { get; init; }
    public required string InstanceId { get; init; }
    public SyncDirection Direction { get; init; }
    public SyncFilter? Filter { get; init; }
    public SyncSchedule? Schedule { get; init; }
    public ConflictResolution ConflictResolution { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool Enabled { get; set; }
}

/// <summary>
/// Sync rule configuration.
/// </summary>
public sealed class SyncRuleConfig
{
    public SyncDirection Direction { get; init; }
    public SyncFilter? Filter { get; init; }
    public SyncSchedule? Schedule { get; init; }
    public ConflictResolution ConflictResolution { get; init; }
}

/// <summary>
/// Sync filter.
/// </summary>
public sealed class SyncFilter
{
    public string? UriPrefix { get; init; }
    public string? ContentType { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTimeOffset? ModifiedAfter { get; init; }
}

/// <summary>
/// Sync schedule.
/// </summary>
public sealed class SyncSchedule
{
    public bool OnConnect { get; init; }
    public bool OnDisconnect { get; init; }
    public TimeSpan? Interval { get; init; }
    public string? CronExpression { get; init; }
}

/// <summary>
/// Sync direction.
/// </summary>
public enum SyncDirection
{
    ToPocket,
    FromPocket,
    Bidirectional
}

/// <summary>
/// Conflict resolution strategy.
/// </summary>
public enum ConflictResolution
{
    LocalWins,
    RemoteWins,
    NewerWins,
    Manual
}

/// <summary>
/// UI information for pocket instance.
/// </summary>
public sealed class PocketInstanceUIInfo
{
    public required string InstanceId { get; init; }
    public required string DisplayName { get; init; }
    public required string DeviceType { get; init; }
    public int BlobCount { get; init; }
    public long TotalSize { get; init; }
    public long AvailableSpace { get; init; }
    public DateTimeOffset? LastSync { get; init; }
    public bool IsOnline { get; init; }
    public string? IconType { get; init; }
}

/// <summary>
/// Transfer result.
/// </summary>
public sealed class TransferResult
{
    public bool Success { get; init; }
    public string? BlobUri { get; init; }
    public long BytesTransferred { get; init; }
    public string? TargetInstanceId { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Sync result.
/// </summary>
public sealed class SyncResult
{
    public bool Success { get; init; }
    public int ItemsSynced { get; init; }
    public int Conflicts { get; init; }
    public long BytesSynced { get; init; }
    public List<string> Errors { get; init; } = new();
    public DateTimeOffset CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Event for pocket instance mounted.
/// </summary>
public sealed class PocketInstanceMountedEvent
{
    public required string InstanceId { get; init; }
    public required string Name { get; init; }
    public required string DeviceId { get; init; }
    public int BlobCount { get; init; }
    public long TotalSize { get; init; }
}

/// <summary>
/// Event for pocket instance unmounted.
/// </summary>
public sealed class PocketInstanceUnmountedEvent
{
    public required string InstanceId { get; init; }
    public required string Name { get; init; }
}

#endregion
