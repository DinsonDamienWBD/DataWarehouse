namespace DataWarehouse.SDK.Federation;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Manifest for a dormant (offline/USB) node.
/// Contains all information needed to "hydrate" the node when connected.
/// </summary>
public sealed class DormantNodeManifest
{
    /// <summary>Version of the manifest format.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Node identity (public info only, no private key).</summary>
    public NodeIdentity Identity { get; set; } = new();

    /// <summary>Objects stored on this dormant node.</summary>
    public List<DormantObjectEntry> Objects { get; set; } = new();

    /// <summary>Capability tokens held by this node.</summary>
    public List<CapabilityToken> Capabilities { get; set; } = new();

    /// <summary>When this manifest was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this manifest was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Parent node that created this dormant node.</summary>
    public string? ParentNodeId { get; set; }

    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Manifest filename on dormant storage.</summary>
    public const string ManifestFileName = ".dw-dormant.json";

    /// <summary>Objects directory on dormant storage.</summary>
    public const string ObjectsDirectory = ".dw-objects";

    /// <summary>
    /// Serializes manifest to JSON.
    /// </summary>
    public string ToJson() => JsonSerializer.Serialize(this, DormantNodeJsonContext.Default.DormantNodeManifest);

    /// <summary>
    /// Deserializes manifest from JSON.
    /// </summary>
    public static DormantNodeManifest? FromJson(string json) =>
        JsonSerializer.Deserialize(json, DormantNodeJsonContext.Default.DormantNodeManifest);

    /// <summary>
    /// Total size of all objects in bytes.
    /// </summary>
    public long TotalSizeBytes => Objects.Sum(o => o.SizeBytes);
}

/// <summary>
/// Entry for an object in a dormant node.
/// </summary>
public sealed class DormantObjectEntry
{
    /// <summary>Object identifier.</summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>Object name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Content type.</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Relative path within objects directory.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Whether this is a chunked object.</summary>
    public bool IsChunked { get; set; }

    /// <summary>Chunk IDs if chunked.</summary>
    public List<string> ChunkIds { get; set; } = new();

    /// <summary>When this object was added.</summary>
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Service for dehydrating (exporting) active nodes to dormant storage.
/// </summary>
public sealed class NodeDehydrator
{
    private readonly NodeIdentityManager _identityManager;
    private readonly ContentAddressableObjectStore _objectStore;
    private readonly CapabilityStore _capabilityStore;

    public NodeDehydrator(
        NodeIdentityManager identityManager,
        ContentAddressableObjectStore objectStore,
        CapabilityStore capabilityStore)
    {
        _identityManager = identityManager;
        _objectStore = objectStore;
        _capabilityStore = capabilityStore;
    }

    /// <summary>
    /// Dehydrates (exports) selected objects to a dormant node location.
    /// Creates a new portable instance on USB/external storage.
    /// </summary>
    public async Task<DehydrateResult> DehydrateAsync(
        string targetPath,
        IEnumerable<ObjectId> objectIds,
        string? nodeName = null,
        CancellationToken ct = default)
    {
        var result = new DehydrateResult { TargetPath = targetPath };

        try
        {
            // Create directories
            Directory.CreateDirectory(targetPath);
            var objectsPath = Path.Combine(targetPath, DormantNodeManifest.ObjectsDirectory);
            Directory.CreateDirectory(objectsPath);

            // Generate new identity for dormant node
            var dormantId = NodeId.NewRandom();
            var identity = new NodeIdentity
            {
                Id = dormantId,
                Name = nodeName ?? $"Dormant-{dormantId.ToShortString()}",
                State = NodeState.Dormant,
                Capabilities = NodeCapabilities.Storage,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow
            };

            var manifest = new DormantNodeManifest
            {
                Identity = identity,
                ParentNodeId = _identityManager.LocalIdentity.Id.ToHex(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            // Copy objects
            foreach (var objectId in objectIds)
            {
                ct.ThrowIfCancellationRequested();

                var retrieveResult = await _objectStore.RetrieveAsync(objectId, ct);
                if (!retrieveResult.Success || retrieveResult.Data == null)
                {
                    result.FailedObjects.Add(objectId.ToHex());
                    continue;
                }

                // Write object to dormant storage
                var relativePath = $"{objectId.ToHex()[..2]}/{objectId.ToHex()}";
                var objectPath = Path.Combine(objectsPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);

                await File.WriteAllBytesAsync(objectPath, retrieveResult.Data, ct);

                manifest.Objects.Add(new DormantObjectEntry
                {
                    ObjectId = objectId.ToHex(),
                    Name = retrieveResult.Manifest?.Name ?? objectId.ToShortString(),
                    SizeBytes = retrieveResult.Data.Length,
                    ContentType = retrieveResult.Manifest?.ContentType ?? "application/octet-stream",
                    RelativePath = relativePath,
                    AddedAt = DateTimeOffset.UtcNow
                });

                result.CopiedObjects.Add(objectId.ToHex());
            }

            // Copy relevant capabilities
            var holderId = _identityManager.LocalIdentity.Id.ToHex();
            var caps = await _capabilityStore.GetTokensForHolderAsync(holderId, ct);

            foreach (var cap in caps)
            {
                // Include capabilities for objects we copied
                if (cap.Type == CapabilityType.Object &&
                    result.CopiedObjects.Contains(cap.TargetId))
                {
                    manifest.Capabilities.Add(cap);
                }
            }

            // Write manifest
            var manifestPath = Path.Combine(targetPath, DormantNodeManifest.ManifestFileName);
            await File.WriteAllTextAsync(manifestPath, manifest.ToJson(), ct);

            result.Success = true;
            result.Manifest = manifest;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }
}

/// <summary>
/// Result of dehydration operation.
/// </summary>
public sealed class DehydrateResult
{
    public bool Success { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public DormantNodeManifest? Manifest { get; set; }
    public List<string> CopiedObjects { get; } = new();
    public List<string> FailedObjects { get; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Service for hydrating (importing) dormant nodes into active instances.
/// </summary>
public sealed class NodeHydrator
{
    private readonly NodeIdentityManager _identityManager;
    private readonly ContentAddressableObjectStore _objectStore;
    private readonly CapabilityStore _capabilityStore;
    private readonly NodeRegistry _nodeRegistry;

    public NodeHydrator(
        NodeIdentityManager identityManager,
        ContentAddressableObjectStore objectStore,
        CapabilityStore capabilityStore,
        NodeRegistry nodeRegistry)
    {
        _identityManager = identityManager;
        _objectStore = objectStore;
        _capabilityStore = capabilityStore;
        _nodeRegistry = nodeRegistry;
    }

    /// <summary>
    /// Hydrates (imports) a dormant node into the active instance.
    /// Makes USB/external storage objects available locally.
    /// </summary>
    public async Task<HydrateResult> HydrateAsync(
        string sourcePath,
        HydrateMode mode = HydrateMode.Reference,
        CancellationToken ct = default)
    {
        var result = new HydrateResult { SourcePath = sourcePath };

        try
        {
            // Read manifest
            var manifestPath = Path.Combine(sourcePath, DormantNodeManifest.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                result.Success = false;
                result.ErrorMessage = "Dormant node manifest not found";
                return result;
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, ct);
            var manifest = DormantNodeManifest.FromJson(manifestJson);

            if (manifest == null)
            {
                result.Success = false;
                result.ErrorMessage = "Invalid manifest format";
                return result;
            }

            result.Manifest = manifest;

            // Register dormant node in registry
            manifest.Identity.State = NodeState.Dormant;
            manifest.Identity.Endpoints.Add(new NodeEndpoint
            {
                Protocol = TransportProtocol.File,
                Path = sourcePath
            });
            _nodeRegistry.Register(manifest.Identity);

            var objectsPath = Path.Combine(sourcePath, DormantNodeManifest.ObjectsDirectory);

            // Process objects based on mode
            foreach (var entry in manifest.Objects)
            {
                ct.ThrowIfCancellationRequested();

                var objectPath = Path.Combine(objectsPath, entry.RelativePath);
                if (!File.Exists(objectPath))
                {
                    result.FailedObjects.Add(entry.ObjectId);
                    continue;
                }

                var objectId = ObjectId.FromHex(entry.ObjectId);

                switch (mode)
                {
                    case HydrateMode.Copy:
                        // Copy object data to local store
                        var data = await File.ReadAllBytesAsync(objectPath, ct);
                        using (var ms = new MemoryStream(data))
                        {
                            await _objectStore.StoreAsync(ms, entry.Name, entry.ContentType, ct);
                        }
                        result.ImportedObjects.Add(entry.ObjectId);
                        break;

                    case HydrateMode.Reference:
                        // Just register the location, don't copy
                        result.ReferencedObjects.Add(entry.ObjectId);
                        break;

                    case HydrateMode.Move:
                        // Copy then mark for deletion
                        var moveData = await File.ReadAllBytesAsync(objectPath, ct);
                        using (var moveMs = new MemoryStream(moveData))
                        {
                            await _objectStore.StoreAsync(moveMs, entry.Name, entry.ContentType, ct);
                        }
                        result.ImportedObjects.Add(entry.ObjectId);
                        result.ObjectsToDelete.Add(objectPath);
                        break;
                }
            }

            // Import capabilities
            foreach (var cap in manifest.Capabilities)
            {
                await _capabilityStore.StoreTokenAsync(cap, ct);
                result.ImportedCapabilities++;
            }

            // Update manifest with hydration timestamp
            manifest.UpdatedAt = DateTimeOffset.UtcNow;
            manifest.Metadata["LastHydratedBy"] = _identityManager.LocalIdentity.Id.ToHex();
            manifest.Metadata["LastHydratedAt"] = DateTimeOffset.UtcNow.ToString("O");

            await File.WriteAllTextAsync(manifestPath, manifest.ToJson(), ct);

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Detects if a path contains a dormant node.
    /// </summary>
    public static bool IsDormantNode(string path)
    {
        var manifestPath = Path.Combine(path, DormantNodeManifest.ManifestFileName);
        return File.Exists(manifestPath);
    }

    /// <summary>
    /// Gets the manifest from a dormant node path.
    /// </summary>
    public static async Task<DormantNodeManifest?> GetManifestAsync(string path, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(path, DormantNodeManifest.ManifestFileName);
        if (!File.Exists(manifestPath))
            return null;

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        return DormantNodeManifest.FromJson(json);
    }
}

/// <summary>
/// Mode for hydrating dormant nodes.
/// </summary>
public enum HydrateMode
{
    /// <summary>Reference objects without copying (read from USB directly).</summary>
    Reference,
    /// <summary>Copy objects to local storage.</summary>
    Copy,
    /// <summary>Move objects to local storage (copy then delete from dormant).</summary>
    Move
}

/// <summary>
/// Result of hydration operation.
/// </summary>
public sealed class HydrateResult
{
    public bool Success { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public DormantNodeManifest? Manifest { get; set; }
    public List<string> ImportedObjects { get; } = new();
    public List<string> ReferencedObjects { get; } = new();
    public List<string> FailedObjects { get; } = new();
    public List<string> ObjectsToDelete { get; } = new();
    public int ImportedCapabilities { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Watcher for dormant node connections (USB plug events).
/// </summary>
public sealed class DormantNodeWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Action<string> _onDormantNodeDetected;
    private bool _disposed;

    public DormantNodeWatcher(Action<string> onDormantNodeDetected)
    {
        _onDormantNodeDetected = onDormantNodeDetected;
    }

    /// <summary>
    /// Starts watching for dormant nodes at specified paths.
    /// </summary>
    public void Watch(IEnumerable<string> watchPaths)
    {
        foreach (var path in watchPaths.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true
            };

            watcher.Created += OnFileCreated;
            watcher.EnableRaisingEvents = true;

            _watchers.Add(watcher);

            // Check existing
            ScanForDormantNodes(path);
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (e.Name == DormantNodeManifest.ManifestFileName)
        {
            var dormantPath = Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrEmpty(dormantPath))
            {
                _onDormantNodeDetected(dormantPath);
            }
        }
    }

    private void ScanForDormantNodes(string basePath)
    {
        try
        {
            // Check immediate subdirectories for dormant manifests
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                if (NodeHydrator.IsDormantNode(dir))
                {
                    _onDormantNodeDetected(dir);
                }
            }
        }
        catch
        {
            // Ignore access errors
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _disposed = true;
        }
    }
}

/// <summary>
/// JSON serialization context for dormant nodes.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DormantNodeManifest))]
[JsonSerializable(typeof(DormantObjectEntry))]
[JsonSerializable(typeof(List<DormantObjectEntry>))]
[JsonSerializable(typeof(List<CapabilityToken>))]
internal partial class DormantNodeJsonContext : JsonSerializerContext
{
}
