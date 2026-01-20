namespace DataWarehouse.Kernel.Federation;

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation;
using DataWarehouse.SDK.Primitives;

/// <summary>
/// Federated storage provider that bridges IStorageProvider to federation.
/// Provides backward compatibility with existing storage APIs while
/// enabling federation capabilities.
/// </summary>
public sealed class FederatedStorageProvider : IStorageProvider
{
    private readonly IFederationHub _hub;
    private readonly IStorageProvider? _localProvider;
    private readonly FederatedStorageOptions _options;

    /// <summary>
    /// Creates a new federated storage provider.
    /// </summary>
    /// <param name="hub">Federation hub for distributed operations.</param>
    /// <param name="localProvider">Optional local provider for hybrid mode.</param>
    /// <param name="options">Configuration options.</param>
    public FederatedStorageProvider(
        IFederationHub hub,
        IStorageProvider? localProvider = null,
        FederatedStorageOptions? options = null)
    {
        _hub = hub;
        _localProvider = localProvider;
        _options = options ?? new FederatedStorageOptions();
    }

    /// <inheritdoc />
    public string Name => "FederatedStorage";

    /// <inheritdoc />
    public bool SupportsStreaming => true;

    /// <inheritdoc />
    public async Task<StoreResult> StoreAsync(
        Stream content,
        Manifest manifest,
        CancellationToken ct = default)
    {
        // Store via federation
        var result = await _hub.StoreAsync(
            content,
            manifest.Name,
            manifest.ContentType,
            ct);

        if (!result.Success)
        {
            return new StoreResult
            {
                Success = false,
                ErrorMessage = result.ErrorMessage
            };
        }

        // Update manifest with federation info
        manifest.Checksum = result.ObjectId.ToHex();
        manifest.Metadata["FederationObjectId"] = result.ObjectId.ToHex();
        manifest.Metadata["FederationPath"] = result.VirtualPath ?? "";
        manifest.Metadata["ReplicaCount"] = result.ReplicaCount.ToString();

        return new StoreResult
        {
            Success = true,
            Manifest = manifest
        };
    }

    /// <inheritdoc />
    public async Task<RetrieveResult> RetrieveAsync(
        Manifest manifest,
        CancellationToken ct = default)
    {
        // Try to get federation object ID from manifest
        ObjectId objectId;

        if (manifest.Metadata.TryGetValue("FederationObjectId", out var fidStr))
        {
            objectId = ObjectId.FromHex(fidStr);
        }
        else if (!string.IsNullOrEmpty(manifest.Checksum))
        {
            objectId = ObjectId.FromHex(manifest.Checksum);
        }
        else
        {
            // Fall back to local provider if available
            if (_localProvider != null)
            {
                return await _localProvider.RetrieveAsync(manifest, ct);
            }

            return new RetrieveResult
            {
                Success = false,
                ErrorMessage = "No federation object ID found"
            };
        }

        var result = await _hub.RetrieveAsync(objectId, ct);

        if (!result.Success)
        {
            // Try local provider as fallback
            if (_localProvider != null && _options.FallbackToLocal)
            {
                return await _localProvider.RetrieveAsync(manifest, ct);
            }

            return new RetrieveResult
            {
                Success = false,
                ErrorMessage = result.ErrorMessage
            };
        }

        return new RetrieveResult
        {
            Success = true,
            Data = result.Data,
            Manifest = manifest
        };
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Manifest manifest, CancellationToken ct = default)
    {
        // Delete from VFS (actual object data retained for other references)
        if (manifest.Metadata.TryGetValue("FederationPath", out var path) && !string.IsNullOrEmpty(path))
        {
            var vfsPath = VfsPath.Parse(path);
            return await _hub.VirtualFilesystem.DeleteAsync(vfsPath, false, ct);
        }

        // Fall back to local
        if (_localProvider != null)
        {
            return await _localProvider.DeleteAsync(manifest, ct);
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Manifest manifest, CancellationToken ct = default)
    {
        if (manifest.Metadata.TryGetValue("FederationPath", out var path) && !string.IsNullOrEmpty(path))
        {
            var vfsPath = VfsPath.Parse(path);
            return await _hub.VirtualFilesystem.ExistsAsync(vfsPath, ct);
        }

        if (manifest.Metadata.TryGetValue("FederationObjectId", out var fidStr))
        {
            var objectId = ObjectId.FromHex(fidStr);
            var resolution = await _hub.ObjectResolver.ResolveAsync(objectId, ct);
            return resolution.Found;
        }

        // Fall back to local
        if (_localProvider != null)
        {
            return await _localProvider.ExistsAsync(manifest, ct);
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Manifest>> ListAsync(
        string? prefix = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var manifests = new List<Manifest>();
        var basePath = string.IsNullOrEmpty(prefix) ? VfsPath.Root : VfsPath.Parse("/" + prefix);

        await foreach (var node in ((VirtualFilesystem)_hub.VirtualFilesystem).EnumerateAsync(basePath, recursive: true, ct: ct))
        {
            if (node.IsFile && node.ObjectId.HasValue)
            {
                manifests.Add(new Manifest
                {
                    Id = node.ObjectId.Value.ToHex(),
                    Name = node.Name,
                    ContentType = node.ContentType,
                    SizeBytes = node.SizeBytes,
                    CreatedAt = node.CreatedAt.ToUnixTimeSeconds(),
                    Metadata = new Dictionary<string, string>
                    {
                        ["FederationObjectId"] = node.ObjectId.Value.ToHex(),
                        ["FederationPath"] = node.Path.ToString()
                    }
                });

                if (limit.HasValue && manifests.Count >= limit.Value)
                    break;
            }
        }

        return manifests;
    }

    /// <inheritdoc />
    public Task<StorageStats> GetStatsAsync(CancellationToken ct = default)
    {
        // Aggregate stats from federation
        var localNode = _hub.LocalNode;

        return Task.FromResult(new StorageStats
        {
            TotalBytes = localNode.StorageCapacityBytes,
            UsedBytes = localNode.StorageCapacityBytes - localNode.StorageAvailableBytes,
            ObjectCount = 0, // Would need to query VFS
            ProviderName = Name
        });
    }
}

/// <summary>
/// Options for federated storage provider.
/// </summary>
public sealed class FederatedStorageOptions
{
    /// <summary>Whether to fall back to local provider on federation failure.</summary>
    public bool FallbackToLocal { get; set; } = true;

    /// <summary>Whether to replicate local-only objects to federation.</summary>
    public bool ReplicateLocalObjects { get; set; } = false;

    /// <summary>Preferred replication factor for new objects.</summary>
    public int ReplicationFactor { get; set; } = 3;

    /// <summary>Whether to cache remote objects locally.</summary>
    public bool CacheRemoteObjects { get; set; } = true;
}

/// <summary>
/// Result of a store operation.
/// </summary>
public sealed class StoreResult
{
    public bool Success { get; init; }
    public Manifest? Manifest { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result of a retrieve operation.
/// </summary>
public sealed class RetrieveResult
{
    public bool Success { get; init; }
    public Stream? Data { get; init; }
    public Manifest? Manifest { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Storage statistics.
/// </summary>
public sealed class StorageStats
{
    public long TotalBytes { get; init; }
    public long UsedBytes { get; init; }
    public long ObjectCount { get; init; }
    public string ProviderName { get; init; } = string.Empty;
}

/// <summary>
/// Migration helper for moving from legacy storage to federated storage.
/// </summary>
public sealed class FederationMigrator
{
    private readonly IFederationHub _hub;
    private readonly IStorageProvider _legacyProvider;

    public FederationMigrator(IFederationHub hub, IStorageProvider legacyProvider)
    {
        _hub = hub;
        _legacyProvider = legacyProvider;
    }

    /// <summary>
    /// Migrates all objects from legacy storage to federation.
    /// </summary>
    public async Task<MigrationResult> MigrateAllAsync(
        IProgress<MigrationProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new MigrationResult();
        var manifests = await _legacyProvider.ListAsync(ct: ct);
        var total = manifests.Count;
        var current = 0;

        foreach (var manifest in manifests)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // Retrieve from legacy
                var retrieveResult = await _legacyProvider.RetrieveAsync(manifest, ct);
                if (!retrieveResult.Success || retrieveResult.Data == null)
                {
                    result.FailedObjects.Add(manifest.Id);
                    result.FailedCount++;
                    continue;
                }

                // Store in federation
                var storeResult = await _hub.StoreAsync(
                    retrieveResult.Data,
                    manifest.Name,
                    manifest.ContentType,
                    ct);

                if (storeResult.Success)
                {
                    result.MigratedObjects.Add((manifest.Id, storeResult.ObjectId.ToHex()));
                    result.MigratedCount++;
                }
                else
                {
                    result.FailedObjects.Add(manifest.Id);
                    result.FailedCount++;
                }
            }
            catch
            {
                result.FailedObjects.Add(manifest.Id);
                result.FailedCount++;
            }

            current++;
            progress?.Report(new MigrationProgress
            {
                Current = current,
                Total = total,
                CurrentObjectId = manifest.Id
            });
        }

        result.Success = result.FailedCount == 0;
        return result;
    }

    /// <summary>
    /// Migrates a single object.
    /// </summary>
    public async Task<bool> MigrateObjectAsync(Manifest manifest, CancellationToken ct = default)
    {
        var retrieveResult = await _legacyProvider.RetrieveAsync(manifest, ct);
        if (!retrieveResult.Success || retrieveResult.Data == null)
            return false;

        var storeResult = await _hub.StoreAsync(
            retrieveResult.Data,
            manifest.Name,
            manifest.ContentType,
            ct);

        return storeResult.Success;
    }
}

/// <summary>
/// Result of migration operation.
/// </summary>
public sealed class MigrationResult
{
    public bool Success { get; set; }
    public int MigratedCount { get; set; }
    public int FailedCount { get; set; }
    public List<(string LegacyId, string FederationId)> MigratedObjects { get; } = new();
    public List<string> FailedObjects { get; } = new();
}

/// <summary>
/// Progress of migration operation.
/// </summary>
public sealed class MigrationProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentObjectId { get; set; } = string.Empty;
    public float PercentComplete => Total > 0 ? (float)Current / Total * 100 : 0;
}

/// <summary>
/// Interface for storage providers (minimal for backward compat).
/// </summary>
public interface IStorageProvider
{
    string Name { get; }
    bool SupportsStreaming { get; }
    Task<StoreResult> StoreAsync(Stream content, Manifest manifest, CancellationToken ct = default);
    Task<RetrieveResult> RetrieveAsync(Manifest manifest, CancellationToken ct = default);
    Task<bool> DeleteAsync(Manifest manifest, CancellationToken ct = default);
    Task<bool> ExistsAsync(Manifest manifest, CancellationToken ct = default);
    Task<IReadOnlyList<Manifest>> ListAsync(string? prefix = null, int? limit = null, CancellationToken ct = default);
    Task<StorageStats> GetStatsAsync(CancellationToken ct = default);
}
