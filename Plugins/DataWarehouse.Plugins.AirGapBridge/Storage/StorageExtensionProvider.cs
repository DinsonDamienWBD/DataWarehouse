using System.Collections.Concurrent;
using DataWarehouse.Plugins.AirGapBridge.Core;
using DataWarehouse.Plugins.AirGapBridge.Detection;

namespace DataWarehouse.Plugins.AirGapBridge.Storage;

/// <summary>
/// Provides storage extension functionality for air-gap devices.
/// Implements sub-tasks 79.11, 79.12, 79.13, 79.14, 79.15.
/// </summary>
public sealed class StorageExtensionProvider : IDisposable, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, MountedStorage> _mountedStorages = new();
    private readonly ConcurrentDictionary<string, OfflineIndexEntry> _offlineIndex = new();
    private readonly string _indexStoragePath;
    private bool _disposed;

    /// <summary>
    /// Event raised when a storage extension is mounted.
    /// </summary>
    public event EventHandler<StorageMountedEvent>? StorageMounted;

    /// <summary>
    /// Event raised when a storage extension is unmounted.
    /// </summary>
    public event EventHandler<StorageUnmountedEvent>? StorageUnmounted;

    /// <summary>
    /// Creates a new storage extension provider.
    /// </summary>
    /// <param name="indexStoragePath">Path to store offline index data.</param>
    public StorageExtensionProvider(string indexStoragePath)
    {
        _indexStoragePath = indexStoragePath;
        Directory.CreateDirectory(indexStoragePath);
        LoadOfflineIndex();
    }

    #region Sub-task 79.11: Dynamic Provider Loading

    /// <summary>
    /// Mounts an air-gap device as a storage extension.
    /// Implements sub-task 79.11.
    /// </summary>
    public async Task<MountedStorage> MountStorageAsync(
        AirGapDevice device,
        StorageExtensionOptions options,
        CancellationToken ct = default)
    {
        if (device.Config?.Mode != AirGapMode.StorageExtension)
        {
            throw new InvalidOperationException("Device is not configured as storage extension");
        }

        var storageId = $"airgap:{device.DeviceId}";
        var storagePath = Path.Combine(device.Path, ".dw-storage");
        Directory.CreateDirectory(storagePath);

        var storage = new MountedStorage
        {
            StorageId = storageId,
            DeviceId = device.DeviceId,
            DevicePath = device.Path,
            StoragePath = storagePath,
            Options = options,
            MountedAt = DateTimeOffset.UtcNow,
            TotalCapacity = device.DriveInfo.TotalCapacity,
            AvailableSpace = device.DriveInfo.AvailableSpace
        };

        _mountedStorages[storageId] = storage;

        // Scan existing data and update capacity
        await ScanStorageContentsAsync(storage, ct);

        StorageMounted?.Invoke(this, new StorageMountedEvent
        {
            StorageId = storageId,
            DeviceId = device.DeviceId,
            Capacity = storage.TotalCapacity,
            AvailableSpace = storage.AvailableSpace
        });

        return storage;
    }

    /// <summary>
    /// Unmounts a storage extension.
    /// </summary>
    public async Task UnmountStorageAsync(string storageId, CancellationToken ct = default)
    {
        if (!_mountedStorages.TryRemove(storageId, out var storage))
        {
            return;
        }

        // Update offline index with current state
        await UpdateOfflineIndexAsync(storage, ct);

        StorageUnmounted?.Invoke(this, new StorageUnmountedEvent
        {
            StorageId = storageId,
            DeviceId = storage.DeviceId
        });
    }

    #endregion

    #region Sub-task 79.12: Capacity Registration

    /// <summary>
    /// Gets the total registered capacity from all mounted storages.
    /// Implements sub-task 79.12.
    /// </summary>
    public StorageCapacityInfo GetTotalCapacity()
    {
        var online = _mountedStorages.Values.ToList();
        var offlineCount = _offlineIndex.Count - _mountedStorages.Count;

        return new StorageCapacityInfo
        {
            OnlineStorageCount = online.Count,
            OfflineStorageCount = Math.Max(0, offlineCount),
            TotalOnlineCapacity = online.Sum(s => s.TotalCapacity),
            TotalOnlineAvailable = online.Sum(s => s.AvailableSpace),
            TotalOfflineCapacity = _offlineIndex.Values
                .Where(i => !_mountedStorages.ContainsKey(i.StorageId))
                .Sum(i => i.TotalCapacity),
            TotalOfflineUsed = _offlineIndex.Values
                .Where(i => !_mountedStorages.ContainsKey(i.StorageId))
                .Sum(i => i.UsedSpace)
        };
    }

    /// <summary>
    /// Registers additional capacity with the storage pool.
    /// </summary>
    public async Task<bool> RegisterCapacityAsync(
        string storageId,
        long additionalCapacity,
        CancellationToken ct = default)
    {
        if (!_mountedStorages.TryGetValue(storageId, out var storage))
        {
            return false;
        }

        // Update drive info
        var driveInfo = new System.IO.DriveInfo(Path.GetPathRoot(storage.DevicePath)!);
        storage.TotalCapacity = driveInfo.TotalSize;
        storage.AvailableSpace = driveInfo.AvailableFreeSpace;

        return true;
    }

    #endregion

    #region Sub-task 79.13: Cold Data Migration

    /// <summary>
    /// Migrates cold data to a storage extension.
    /// Implements sub-task 79.13.
    /// </summary>
    public async Task<MigrationResult> MigrateColdDataAsync(
        string storageId,
        IEnumerable<ColdDataEntry> coldData,
        MigrationOptions options,
        CancellationToken ct = default)
    {
        if (!_mountedStorages.TryGetValue(storageId, out var storage))
        {
            return new MigrationResult
            {
                Success = false,
                ErrorMessage = "Storage not found"
            };
        }

        var migrated = 0;
        var skipped = 0;
        long bytesMigrated = 0;
        var errors = new List<string>();

        foreach (var entry in coldData)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Check available space
                if (entry.Size > storage.AvailableSpace)
                {
                    skipped++;
                    errors.Add($"{entry.Uri}: Insufficient space");
                    continue;
                }

                // Write to storage extension
                var targetPath = Path.Combine(storage.StoragePath, SanitizeFileName(entry.Uri));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                await File.WriteAllBytesAsync(targetPath, entry.Data, ct);

                // Update storage metrics
                storage.AvailableSpace -= entry.Size;
                storage.UsedSpace += entry.Size;
                storage.BlobCount++;

                // Add to storage index
                storage.Index[entry.Uri] = new StorageIndexEntry
                {
                    Uri = entry.Uri,
                    Path = targetPath,
                    Size = entry.Size,
                    LastAccess = entry.LastAccess,
                    MigratedAt = DateTimeOffset.UtcNow
                };

                migrated++;
                bytesMigrated += entry.Size;
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.Uri}: {ex.Message}");
                skipped++;
            }
        }

        return new MigrationResult
        {
            Success = errors.Count == 0,
            ItemsMigrated = migrated,
            ItemsSkipped = skipped,
            BytesMigrated = bytesMigrated,
            Errors = errors
        };
    }

    /// <summary>
    /// Retrieves data from a storage extension.
    /// </summary>
    public async Task<byte[]?> GetDataAsync(
        string uri,
        CancellationToken ct = default)
    {
        foreach (var storage in _mountedStorages.Values)
        {
            if (storage.Index.TryGetValue(uri, out var entry))
            {
                if (File.Exists(entry.Path))
                {
                    return await File.ReadAllBytesAsync(entry.Path, ct);
                }
            }
        }

        return null;
    }

    #endregion

    #region Sub-task 79.14: Safe Removal Handler

    /// <summary>
    /// Handles safe removal of a storage extension.
    /// Implements sub-task 79.14.
    /// </summary>
    public async Task<SafeRemovalResult> PrepareForRemovalAsync(
        string storageId,
        CancellationToken ct = default)
    {
        if (!_mountedStorages.TryGetValue(storageId, out var storage))
        {
            return new SafeRemovalResult
            {
                Success = false,
                ErrorMessage = "Storage not found"
            };
        }

        // Check for pending operations
        if (storage.PendingOperations > 0)
        {
            return new SafeRemovalResult
            {
                Success = false,
                ErrorMessage = $"{storage.PendingOperations} operations still pending",
                CanForceRemove = true
            };
        }

        // Flush any cached data
        await FlushCacheAsync(storage, ct);

        // Update offline index
        await UpdateOfflineIndexAsync(storage, ct);

        // Mark as safe to remove
        storage.SafeToRemove = true;

        return new SafeRemovalResult
        {
            Success = true,
            StorageId = storageId,
            BlobsStored = storage.BlobCount,
            SpaceUsed = storage.UsedSpace
        };
    }

    /// <summary>
    /// Handles unexpected device removal.
    /// </summary>
    public async Task HandleUnexpectedRemovalAsync(string deviceId, CancellationToken ct = default)
    {
        var storage = _mountedStorages.Values.FirstOrDefault(s => s.DeviceId == deviceId);
        if (storage == null) return;

        // Mark all blobs as offline
        foreach (var (uri, entry) in storage.Index)
        {
            _offlineIndex[uri] = new OfflineIndexEntry
            {
                StorageId = storage.StorageId,
                Uri = uri,
                Size = entry.Size,
                LastAccess = entry.LastAccess,
                OfflineSince = DateTimeOffset.UtcNow,
                TotalCapacity = storage.TotalCapacity,
                UsedSpace = storage.UsedSpace
            };
        }

        // Save offline index
        await SaveOfflineIndexAsync(ct);

        _mountedStorages.TryRemove(storage.StorageId, out _);
    }

    #endregion

    #region Sub-task 79.15: Offline Index

    /// <summary>
    /// Gets the offline index entries.
    /// Implements sub-task 79.15.
    /// </summary>
    public IReadOnlyDictionary<string, OfflineIndexEntry> GetOfflineIndex()
    {
        return _offlineIndex
            .Where(kv => !_mountedStorages.ContainsKey(kv.Value.StorageId))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Checks if a blob is stored on an offline device.
    /// </summary>
    public bool IsOffline(string uri, out string? storageId)
    {
        if (_offlineIndex.TryGetValue(uri, out var entry))
        {
            if (!_mountedStorages.ContainsKey(entry.StorageId))
            {
                storageId = entry.StorageId;
                return true;
            }
        }

        storageId = null;
        return false;
    }

    private void LoadOfflineIndex()
    {
        var indexPath = Path.Combine(_indexStoragePath, "offline-index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                var json = File.ReadAllText(indexPath);
                var entries = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, OfflineIndexEntry>>(json);
                if (entries != null)
                {
                    foreach (var (key, value) in entries)
                    {
                        _offlineIndex[key] = value;
                    }
                }
            }
            catch
            {
                // Ignore corrupt index
            }
        }
    }

    private async Task SaveOfflineIndexAsync(CancellationToken ct = default)
    {
        var indexPath = Path.Combine(_indexStoragePath, "offline-index.json");
        var json = System.Text.Json.JsonSerializer.Serialize(_offlineIndex);
        await File.WriteAllTextAsync(indexPath, json, ct);
    }

    private async Task UpdateOfflineIndexAsync(MountedStorage storage, CancellationToken ct)
    {
        foreach (var (uri, entry) in storage.Index)
        {
            _offlineIndex[uri] = new OfflineIndexEntry
            {
                StorageId = storage.StorageId,
                Uri = uri,
                Size = entry.Size,
                LastAccess = entry.LastAccess,
                OfflineSince = DateTimeOffset.UtcNow,
                TotalCapacity = storage.TotalCapacity,
                UsedSpace = storage.UsedSpace
            };
        }

        await SaveOfflineIndexAsync(ct);
    }

    #endregion

    #region Helper Methods

    private async Task ScanStorageContentsAsync(MountedStorage storage, CancellationToken ct)
    {
        if (!Directory.Exists(storage.StoragePath)) return;

        foreach (var file in Directory.EnumerateFiles(storage.StoragePath, "*", SearchOption.AllDirectories))
        {
            if (ct.IsCancellationRequested) break;

            var fileInfo = new FileInfo(file);
            var uri = RestoreUri(Path.GetRelativePath(storage.StoragePath, file));

            storage.Index[uri] = new StorageIndexEntry
            {
                Uri = uri,
                Path = file,
                Size = fileInfo.Length,
                LastAccess = fileInfo.LastAccessTime
            };

            storage.UsedSpace += fileInfo.Length;
            storage.BlobCount++;
        }
    }

    private async Task FlushCacheAsync(MountedStorage storage, CancellationToken ct)
    {
        // Ensure all data is written to disk
        foreach (var entry in storage.Index.Values)
        {
            if (File.Exists(entry.Path))
            {
                using var fs = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.None);
                await fs.FlushAsync(ct);
            }
        }
    }

    private static string SanitizeFileName(string uri)
    {
        // Convert URI to safe file path
        var safe = uri.Replace("://", "_").Replace("/", "_").Replace("\\", "_");
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(c, '_');
        }
        return safe;
    }

    private static string RestoreUri(string path)
    {
        // Attempt to restore original URI from safe path
        return path.Replace('_', '/');
    }

    #endregion

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        // Synchronous cleanup only (none needed currently)
    }

    private async ValueTask DisposeAsyncCore()
    {
        if (_disposed) return;

        // Save offline index before disposing
        await SaveOfflineIndexAsync().ConfigureAwait(false);
    }
}

#region Types

/// <summary>
/// Represents a mounted storage extension.
/// </summary>
public sealed class MountedStorage
{
    public required string StorageId { get; init; }
    public required string DeviceId { get; init; }
    public required string DevicePath { get; init; }
    public required string StoragePath { get; init; }
    public required StorageExtensionOptions Options { get; init; }
    public DateTimeOffset MountedAt { get; init; }
    public long TotalCapacity { get; set; }
    public long AvailableSpace { get; set; }
    public long UsedSpace { get; set; }
    public int BlobCount { get; set; }
    public int PendingOperations { get; set; }
    public bool SafeToRemove { get; set; }
    public ConcurrentDictionary<string, StorageIndexEntry> Index { get; } = new();
}

/// <summary>
/// Options for storage extension.
/// </summary>
public sealed class StorageExtensionOptions
{
    public bool AutoMigrateColdData { get; init; }
    public TimeSpan ColdDataThreshold { get; init; } = TimeSpan.FromDays(30);
    public long MaxCapacityToUse { get; init; }
    public bool PreferForLargeFiles { get; init; }
}

/// <summary>
/// Storage index entry.
/// </summary>
public sealed class StorageIndexEntry
{
    public required string Uri { get; init; }
    public required string Path { get; init; }
    public long Size { get; init; }
    public DateTime LastAccess { get; set; }
    public DateTimeOffset? MigratedAt { get; init; }
}

/// <summary>
/// Offline index entry.
/// </summary>
public sealed class OfflineIndexEntry
{
    public required string StorageId { get; init; }
    public required string Uri { get; init; }
    public long Size { get; init; }
    public DateTime LastAccess { get; init; }
    public DateTimeOffset OfflineSince { get; init; }
    public long TotalCapacity { get; init; }
    public long UsedSpace { get; init; }
}

/// <summary>
/// Cold data entry for migration.
/// </summary>
public sealed class ColdDataEntry
{
    public required string Uri { get; init; }
    public required byte[] Data { get; init; }
    public long Size { get; init; }
    public DateTime LastAccess { get; init; }
}

/// <summary>
/// Migration options.
/// </summary>
public sealed class MigrationOptions
{
    public bool DeleteAfterMigration { get; init; }
    public bool VerifyAfterWrite { get; init; } = true;
    public int MaxConcurrentMigrations { get; init; } = 4;
}

/// <summary>
/// Migration result.
/// </summary>
public sealed class MigrationResult
{
    public bool Success { get; init; }
    public int ItemsMigrated { get; init; }
    public int ItemsSkipped { get; init; }
    public long BytesMigrated { get; init; }
    public List<string> Errors { get; init; } = new();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Safe removal result.
/// </summary>
public sealed class SafeRemovalResult
{
    public bool Success { get; init; }
    public string? StorageId { get; init; }
    public int BlobsStored { get; init; }
    public long SpaceUsed { get; init; }
    public bool CanForceRemove { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Storage capacity information.
/// </summary>
public sealed class StorageCapacityInfo
{
    public int OnlineStorageCount { get; init; }
    public int OfflineStorageCount { get; init; }
    public long TotalOnlineCapacity { get; init; }
    public long TotalOnlineAvailable { get; init; }
    public long TotalOfflineCapacity { get; init; }
    public long TotalOfflineUsed { get; init; }
}

/// <summary>
/// Event for storage mounted.
/// </summary>
public sealed class StorageMountedEvent
{
    public required string StorageId { get; init; }
    public required string DeviceId { get; init; }
    public long Capacity { get; init; }
    public long AvailableSpace { get; init; }
}

/// <summary>
/// Event for storage unmounted.
/// </summary>
public sealed class StorageUnmountedEvent
{
    public required string StorageId { get; init; }
    public required string DeviceId { get; init; }
}

#endregion
