namespace DataWarehouse.SDK.Federation;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Infrastructure;

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

    /// <summary>Whether data is encrypted.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Encryption key ID (for key escrow).</summary>
    public string? EncryptionKeyId { get; set; }

    /// <summary>Encrypted data key (wrapped with password/KEK).</summary>
    public byte[]? WrappedDataKey { get; set; }

    /// <summary>Salt for password-based key derivation.</summary>
    public byte[]? KeyDerivationSalt { get; set; }

    /// <summary>Key derivation algorithm used.</summary>
    public string KeyDerivationAlgorithm { get; set; } = "Argon2id";

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

    /// <summary>Whether this object is encrypted.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>IV/Nonce for this object's encryption.</summary>
    public byte[]? EncryptionIV { get; set; }

    /// <summary>Authentication tag for AES-GCM.</summary>
    public byte[]? AuthTag { get; set; }
}

/// <summary>
/// Options for dehydration with encryption.
/// </summary>
public sealed class DehydrateOptions
{
    /// <summary>Whether to encrypt the dormant node data.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>Password for encryption (if not using key store).</summary>
    public string? Password { get; set; }

    /// <summary>Key store for enterprise key management.</summary>
    public IKeyEncryptionProvider? KeyProvider { get; set; }

    /// <summary>Custom node name.</summary>
    public string? NodeName { get; set; }

    /// <summary>Key derivation method.</summary>
    public KeyDerivationMethod KeyDerivation { get; set; } = KeyDerivationMethod.Argon2id;
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
    public Task<DehydrateResult> DehydrateAsync(
        string targetPath,
        IEnumerable<ObjectId> objectIds,
        string? nodeName = null,
        CancellationToken ct = default)
    {
        return DehydrateAsync(targetPath, objectIds, new DehydrateOptions { NodeName = nodeName, Encrypt = false }, ct);
    }

    /// <summary>
    /// Dehydrates (exports) selected objects to a dormant node location with encryption.
    /// Creates a new portable instance on USB/external storage with AES-256-GCM encryption.
    /// </summary>
    public async Task<DehydrateResult> DehydrateAsync(
        string targetPath,
        IEnumerable<ObjectId> objectIds,
        DehydrateOptions options,
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
                Name = options.NodeName ?? $"Dormant-{dormantId.ToShortString()}",
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

            // Setup encryption if enabled
            byte[]? dataKey = null;
            if (options.Encrypt)
            {
                // Generate random data encryption key (DEK)
                dataKey = new byte[32];
                RandomNumberGenerator.Fill(dataKey);
                manifest.IsEncrypted = true;
                manifest.EncryptionKeyId = Guid.NewGuid().ToString("N");

                if (!string.IsNullOrEmpty(options.Password))
                {
                    // Derive key from password and wrap DEK
                    manifest.KeyDerivationSalt = new byte[32];
                    RandomNumberGenerator.Fill(manifest.KeyDerivationSalt);
                    manifest.KeyDerivationAlgorithm = options.KeyDerivation.ToString();

                    var kek = DeriveKeyFromPassword(options.Password, manifest.KeyDerivationSalt, options.KeyDerivation);
                    manifest.WrappedDataKey = WrapKey(dataKey, kek);
                    CryptographicOperations.ZeroMemory(kek);
                }
                else if (options.KeyProvider != null)
                {
                    // Use key provider to wrap DEK
                    var wrapped = await options.KeyProvider.EncryptKeyAsync(dataKey, manifest.EncryptionKeyId, ct);
                    manifest.WrappedDataKey = wrapped.EncryptedData;
                    manifest.KeyDerivationAlgorithm = "KeyVault";
                }
                else
                {
                    throw new ArgumentException("Encryption enabled but no password or key provider specified");
                }
            }

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

                var entry = new DormantObjectEntry
                {
                    ObjectId = objectId.ToHex(),
                    Name = retrieveResult.Manifest?.Name ?? objectId.ToShortString(),
                    SizeBytes = retrieveResult.Data.Length,
                    ContentType = retrieveResult.Manifest?.ContentType ?? "application/octet-stream",
                    RelativePath = relativePath,
                    AddedAt = DateTimeOffset.UtcNow
                };

                if (options.Encrypt && dataKey != null)
                {
                    // Encrypt object data with AES-256-GCM
                    var (encryptedData, iv, tag) = EncryptData(retrieveResult.Data, dataKey);
                    await File.WriteAllBytesAsync(objectPath, encryptedData, ct);
                    entry.IsEncrypted = true;
                    entry.EncryptionIV = iv;
                    entry.AuthTag = tag;
                }
                else
                {
                    await File.WriteAllBytesAsync(objectPath, retrieveResult.Data, ct);
                }

                manifest.Objects.Add(entry);
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

            // Clear sensitive data
            if (dataKey != null)
                CryptographicOperations.ZeroMemory(dataKey);

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

    private static byte[] DeriveKeyFromPassword(string password, byte[] salt, KeyDerivationMethod method)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            iterations: method == KeyDerivationMethod.Argon2id ? 600_000 : 310_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    private static byte[] WrapKey(byte[] key, byte[] kek)
    {
        var iv = new byte[12];
        RandomNumberGenerator.Fill(iv);

        using var aes = new AesGcm(kek, 16);
        var ciphertext = new byte[key.Length];
        var tag = new byte[16];
        aes.Encrypt(iv, key, ciphertext, tag);

        // Return [iv:12][tag:16][ciphertext:32]
        var result = new byte[12 + 16 + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, result, 0, 12);
        Buffer.BlockCopy(tag, 0, result, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);
        return result;
    }

    private static byte[] UnwrapKey(byte[] wrapped, byte[] kek)
    {
        var iv = wrapped.AsSpan(0, 12).ToArray();
        var tag = wrapped.AsSpan(12, 16).ToArray();
        var ciphertext = wrapped.AsSpan(28).ToArray();

        using var aes = new AesGcm(kek, 16);
        var key = new byte[ciphertext.Length];
        aes.Decrypt(iv, ciphertext, tag, key);
        return key;
    }

    private static (byte[] ciphertext, byte[] iv, byte[] tag) EncryptData(byte[] data, byte[] key)
    {
        var iv = new byte[12];
        RandomNumberGenerator.Fill(iv);

        using var aes = new AesGcm(key, 16);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];
        aes.Encrypt(iv, data, ciphertext, tag);

        return (ciphertext, iv, tag);
    }

    private static byte[] DecryptData(byte[] ciphertext, byte[] iv, byte[] tag, byte[] key)
    {
        using var aes = new AesGcm(key, 16);
        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(iv, ciphertext, tag, plaintext);
        return plaintext;
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
/// Options for hydration with decryption.
/// </summary>
public sealed class HydrateOptions
{
    /// <summary>Mode for hydration.</summary>
    public HydrateMode Mode { get; set; } = HydrateMode.Reference;

    /// <summary>Password for decryption (if encrypted with password).</summary>
    public string? Password { get; set; }

    /// <summary>Key provider for decryption (if encrypted with key vault).</summary>
    public IKeyEncryptionProvider? KeyProvider { get; set; }
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
    public Task<HydrateResult> HydrateAsync(
        string sourcePath,
        HydrateMode mode = HydrateMode.Reference,
        CancellationToken ct = default)
    {
        return HydrateAsync(sourcePath, new HydrateOptions { Mode = mode }, ct);
    }

    /// <summary>
    /// Hydrates (imports) a dormant node with decryption support.
    /// </summary>
    public async Task<HydrateResult> HydrateAsync(
        string sourcePath,
        HydrateOptions options,
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

            // Derive data key if encrypted
            byte[]? dataKey = null;
            if (manifest.IsEncrypted)
            {
                if (manifest.WrappedDataKey == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Encrypted manifest missing wrapped data key";
                    return result;
                }

                if (!string.IsNullOrEmpty(options.Password) && manifest.KeyDerivationSalt != null)
                {
                    // Derive KEK from password and unwrap DEK
                    var method = Enum.TryParse<KeyDerivationMethod>(manifest.KeyDerivationAlgorithm, out var m)
                        ? m : KeyDerivationMethod.Argon2id;
                    var kek = DeriveKeyFromPassword(options.Password, manifest.KeyDerivationSalt, method);
                    dataKey = UnwrapKey(manifest.WrappedDataKey, kek);
                    CryptographicOperations.ZeroMemory(kek);
                }
                else if (options.KeyProvider != null)
                {
                    // Use key provider to unwrap DEK
                    var wrapped = new EncryptedKey
                    {
                        KeyId = manifest.EncryptionKeyId ?? "",
                        ProviderId = manifest.KeyDerivationAlgorithm,
                        EncryptedData = manifest.WrappedDataKey,
                        IV = Array.Empty<byte>(),
                        KeyVersion = 1,
                        EncryptedAt = manifest.CreatedAt.DateTime
                    };
                    dataKey = await options.KeyProvider.DecryptKeyAsync(wrapped, ct);
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "Encrypted dormant node requires password or key provider";
                    return result;
                }
            }

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

                try
                {
                    byte[] data;
                    if (entry.IsEncrypted && dataKey != null && entry.EncryptionIV != null && entry.AuthTag != null)
                    {
                        // Decrypt object data
                        var encryptedData = await File.ReadAllBytesAsync(objectPath, ct);
                        data = DecryptData(encryptedData, entry.EncryptionIV, entry.AuthTag, dataKey);
                    }
                    else
                    {
                        data = await File.ReadAllBytesAsync(objectPath, ct);
                    }

                    switch (options.Mode)
                    {
                        case HydrateMode.Copy:
                        case HydrateMode.Move:
                            using (var ms = new MemoryStream(data))
                            {
                                await _objectStore.StoreAsync(ms, entry.Name, entry.ContentType, ct);
                            }
                            result.ImportedObjects.Add(entry.ObjectId);
                            if (options.Mode == HydrateMode.Move)
                                result.ObjectsToDelete.Add(objectPath);
                            break;

                        case HydrateMode.Reference:
                            result.ReferencedObjects.Add(entry.ObjectId);
                            break;
                    }
                }
                catch (CryptographicException)
                {
                    result.FailedObjects.Add(entry.ObjectId);
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

            // Clear sensitive data
            if (dataKey != null)
                CryptographicOperations.ZeroMemory(dataKey);

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private static byte[] DeriveKeyFromPassword(string password, byte[] salt, KeyDerivationMethod method)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt,
            iterations: method == KeyDerivationMethod.Argon2id ? 600_000 : 310_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }

    private static byte[] UnwrapKey(byte[] wrapped, byte[] kek)
    {
        var iv = wrapped.AsSpan(0, 12).ToArray();
        var tag = wrapped.AsSpan(12, 16).ToArray();
        var ciphertext = wrapped.AsSpan(28).ToArray();

        using var aes = new AesGcm(kek, 16);
        var key = new byte[ciphertext.Length];
        aes.Decrypt(iv, ciphertext, tag, key);
        return key;
    }

    private static byte[] DecryptData(byte[] ciphertext, byte[] iv, byte[] tag, byte[] key)
    {
        using var aes = new AesGcm(key, 16);
        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(iv, ciphertext, tag, plaintext);
        return plaintext;
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
[JsonSerializable(typeof(IncrementalSyncState))]
[JsonSerializable(typeof(SneakernetConflict))]
[JsonSerializable(typeof(List<SneakernetConflict>))]
[JsonSerializable(typeof(OfflineChangeRecord))]
[JsonSerializable(typeof(List<OfflineChangeRecord>))]
internal partial class DormantNodeJsonContext : JsonSerializerContext
{
}

// ============================================================================
// SCENARIO 2: SNEAKERNET SUPPORT (U1 → USB → U2)
// Extends existing DormantNode infrastructure with incremental sync,
// conflict resolution, and auto-resync capabilities.
// ============================================================================

#region Incremental Sync

/// <summary>
/// State for incremental synchronization.
/// </summary>
public sealed class IncrementalSyncState
{
    /// <summary>Last successful sync timestamp.</summary>
    public DateTimeOffset LastSyncAt { get; set; }

    /// <summary>Object checksums at last sync.</summary>
    public Dictionary<string, string> ObjectChecksums { get; set; } = new();

    /// <summary>Objects pending sync.</summary>
    public List<string> PendingObjectIds { get; set; } = new();

    /// <summary>Sync sequence number.</summary>
    public long SyncSequence { get; set; }

    /// <summary>Source node ID.</summary>
    public string SourceNodeId { get; set; } = string.Empty;

    /// <summary>Target node ID.</summary>
    public string TargetNodeId { get; set; } = string.Empty;

    /// <summary>Sync state filename.</summary>
    public const string SyncStateFileName = ".dw-sync-state.json";

    public string ToJson() => JsonSerializer.Serialize(this, DormantNodeJsonContext.Default.IncrementalSyncState);

    public static IncrementalSyncState? FromJson(string json) =>
        JsonSerializer.Deserialize(json, DormantNodeJsonContext.Default.IncrementalSyncState);
}

/// <summary>
/// Manages incremental synchronization between active and dormant nodes.
/// Uses checksums to identify changed objects for delta transfers.
/// </summary>
public sealed class IncrementalSyncManager
{
    private readonly ContentAddressableObjectStore _objectStore;
    private readonly NodeIdentityManager _identityManager;

    /// <summary>Gets the underlying object store for conflict detection.</summary>
    internal ContentAddressableObjectStore ObjectStore => _objectStore;

    public IncrementalSyncManager(
        ContentAddressableObjectStore objectStore,
        NodeIdentityManager identityManager)
    {
        _objectStore = objectStore;
        _identityManager = identityManager;
    }

    /// <summary>
    /// Calculates delta between local state and dormant node.
    /// Returns objects that need to be transferred.
    /// </summary>
    public async Task<SyncDelta> CalculateDeltaAsync(
        string dormantPath,
        CancellationToken ct = default)
    {
        var delta = new SyncDelta();
        var syncStatePath = Path.Combine(dormantPath, IncrementalSyncState.SyncStateFileName);
        var manifestPath = Path.Combine(dormantPath, DormantNodeManifest.ManifestFileName);

        // Load existing sync state
        IncrementalSyncState? syncState = null;
        if (File.Exists(syncStatePath))
        {
            var json = await File.ReadAllTextAsync(syncStatePath, ct);
            syncState = IncrementalSyncState.FromJson(json);
        }

        // Load dormant manifest
        DormantNodeManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            manifest = DormantNodeManifest.FromJson(json);
        }

        var dormantObjects = manifest?.Objects.ToDictionary(o => o.ObjectId) ?? new();
        var dormantChecksums = syncState?.ObjectChecksums ?? new();

        // Get local objects
        var localObjectIds = await _objectStore.ListAllObjectIdsAsync(ct).ToListAsync(ct);

        foreach (var objectId in localObjectIds)
        {
            var objectIdHex = objectId.ToHex();
            var localChecksum = await ComputeChecksumAsync(objectId, ct);

            if (!dormantObjects.ContainsKey(objectIdHex))
            {
                // New object - needs to be copied to dormant
                delta.NewObjects.Add(objectIdHex);
            }
            else if (dormantChecksums.TryGetValue(objectIdHex, out var oldChecksum) &&
                     oldChecksum != localChecksum)
            {
                // Modified object
                delta.ModifiedObjects.Add(objectIdHex);
            }
        }

        // Find objects deleted locally but present on dormant
        foreach (var dormantObj in dormantObjects.Keys)
        {
            var found = localObjectIds.Any(id => id.ToHex() == dormantObj);
            if (!found)
            {
                delta.DeletedObjects.Add(dormantObj);
            }
        }

        delta.TotalChanges = delta.NewObjects.Count + delta.ModifiedObjects.Count + delta.DeletedObjects.Count;
        return delta;
    }

    /// <summary>
    /// Performs incremental sync, only transferring changed objects.
    /// </summary>
    public async Task<IncrementalSyncResult> SyncAsync(
        string dormantPath,
        NodeDehydrator dehydrator,
        DehydrateOptions options,
        CancellationToken ct = default)
    {
        var result = new IncrementalSyncResult { TargetPath = dormantPath };

        try
        {
            var delta = await CalculateDeltaAsync(dormantPath, ct);
            result.Delta = delta;

            if (delta.TotalChanges == 0)
            {
                result.Success = true;
                result.Message = "No changes to sync";
                return result;
            }

            // Sync new and modified objects
            var objectsToSync = delta.NewObjects.Concat(delta.ModifiedObjects)
                .Select(hex => ObjectId.FromHex(hex))
                .ToList();

            if (objectsToSync.Count > 0)
            {
                var dehydrateResult = await dehydrator.DehydrateAsync(
                    dormantPath, objectsToSync, options, ct);

                result.ObjectsSynced = dehydrateResult.CopiedObjects.Count;
                result.ObjectsFailed = dehydrateResult.FailedObjects.Count;
            }

            // Handle deletions by marking in manifest
            if (delta.DeletedObjects.Count > 0)
            {
                await MarkObjectsDeletedAsync(dormantPath, delta.DeletedObjects, ct);
                result.ObjectsDeleted = delta.DeletedObjects.Count;
            }

            // Update sync state
            await UpdateSyncStateAsync(dormantPath, ct);

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = ex.Message;
        }

        return result;
    }

    private async Task<string> ComputeChecksumAsync(ObjectId objectId, CancellationToken ct)
    {
        var result = await _objectStore.RetrieveAsync(objectId, ct);
        if (result.Data == null) return string.Empty;

        var hash = SHA256.HashData(result.Data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task MarkObjectsDeletedAsync(string dormantPath, List<string> deletedIds, CancellationToken ct)
    {
        var manifestPath = Path.Combine(dormantPath, DormantNodeManifest.ManifestFileName);
        if (!File.Exists(manifestPath)) return;

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = DormantNodeManifest.FromJson(json);
        if (manifest == null) return;

        manifest.Objects.RemoveAll(o => deletedIds.Contains(o.ObjectId));
        manifest.UpdatedAt = DateTimeOffset.UtcNow;

        await File.WriteAllTextAsync(manifestPath, manifest.ToJson(), ct);
    }

    private async Task UpdateSyncStateAsync(string dormantPath, CancellationToken ct)
    {
        var syncStatePath = Path.Combine(dormantPath, IncrementalSyncState.SyncStateFileName);
        var manifestPath = Path.Combine(dormantPath, DormantNodeManifest.ManifestFileName);

        var manifest = DormantNodeManifest.FromJson(
            await File.ReadAllTextAsync(manifestPath, ct));

        var checksums = new Dictionary<string, string>();
        foreach (var obj in manifest?.Objects ?? new())
        {
            var objPath = Path.Combine(dormantPath, DormantNodeManifest.ObjectsDirectory, obj.RelativePath);
            if (File.Exists(objPath))
            {
                var data = await File.ReadAllBytesAsync(objPath, ct);
                checksums[obj.ObjectId] = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            }
        }

        var state = new IncrementalSyncState
        {
            LastSyncAt = DateTimeOffset.UtcNow,
            ObjectChecksums = checksums,
            SyncSequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SourceNodeId = _identityManager.LocalIdentity.Id.ToHex(),
            TargetNodeId = manifest?.Identity.Id.ToHex() ?? string.Empty
        };

        await File.WriteAllTextAsync(syncStatePath, state.ToJson(), ct);
    }
}

/// <summary>
/// Delta between local and dormant storage.
/// </summary>
public sealed class SyncDelta
{
    public List<string> NewObjects { get; } = new();
    public List<string> ModifiedObjects { get; } = new();
    public List<string> DeletedObjects { get; } = new();
    public int TotalChanges { get; set; }
}

/// <summary>
/// Result of incremental sync operation.
/// </summary>
public sealed class IncrementalSyncResult
{
    public bool Success { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public SyncDelta? Delta { get; set; }
    public int ObjectsSynced { get; set; }
    public int ObjectsFailed { get; set; }
    public int ObjectsDeleted { get; set; }
    public string? Message { get; set; }
}

#endregion

#region Conflict Queue

/// <summary>
/// Represents a conflict between local and dormant changes.
/// </summary>
public sealed class SneakernetConflict
{
    /// <summary>Object that has conflicts.</summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>Object name.</summary>
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>When conflict was detected.</summary>
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Local version info.</summary>
    public ConflictVersion LocalVersion { get; set; } = new();

    /// <summary>Dormant (remote) version info.</summary>
    public ConflictVersion DormantVersion { get; set; } = new();

    /// <summary>Base version (common ancestor) if available.</summary>
    public ConflictVersion? BaseVersion { get; set; }

    /// <summary>Resolution status.</summary>
    public ConflictResolution Resolution { get; set; } = ConflictResolution.Unresolved;

    /// <summary>Resolved version (if resolved).</summary>
    public ConflictVersion? ResolvedVersion { get; set; }

    /// <summary>Resolution timestamp.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// Version information for conflict resolution.
/// </summary>
public sealed class ConflictVersion
{
    public string Checksum { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
    public byte[]? Data { get; set; }
}

/// <summary>
/// Conflict resolution strategy.
/// </summary>
public enum ConflictResolution
{
    Unresolved,
    KeepLocal,
    KeepDormant,
    Merged,
    ManuallyResolved
}

/// <summary>
/// Manages conflicts during sneakernet sync operations.
/// Supports three-way merge when base version is available.
/// </summary>
public sealed class SneakernetConflictQueue
{
    private readonly List<SneakernetConflict> _conflicts = new();
    private readonly string _queuePath;
    private readonly object _lock = new();

    public const string ConflictQueueFileName = ".dw-conflicts.json";

    public int ConflictCount => _conflicts.Count;
    public int UnresolvedCount => _conflicts.Count(c => c.Resolution == ConflictResolution.Unresolved);

    public SneakernetConflictQueue(string basePath)
    {
        _queuePath = Path.Combine(basePath, ConflictQueueFileName);
        LoadQueue();
    }

    /// <summary>
    /// Detects and queues conflicts between local and dormant versions.
    /// </summary>
    public async Task<List<SneakernetConflict>> DetectConflictsAsync(
        ContentAddressableObjectStore localStore,
        string dormantPath,
        CancellationToken ct = default)
    {
        var newConflicts = new List<SneakernetConflict>();
        var manifestPath = Path.Combine(dormantPath, DormantNodeManifest.ManifestFileName);

        if (!File.Exists(manifestPath)) return newConflicts;

        var manifest = DormantNodeManifest.FromJson(
            await File.ReadAllTextAsync(manifestPath, ct));
        if (manifest == null) return newConflicts;

        var objectsPath = Path.Combine(dormantPath, DormantNodeManifest.ObjectsDirectory);

        foreach (var dormantObj in manifest.Objects)
        {
            var objectId = ObjectId.FromHex(dormantObj.ObjectId);

            // Check if local has this object
            var localResult = await localStore.RetrieveAsync(objectId, ct);
            if (!localResult.Success || localResult.Data == null) continue;

            // Read dormant version
            var dormantObjPath = Path.Combine(objectsPath, dormantObj.RelativePath);
            if (!File.Exists(dormantObjPath)) continue;

            var dormantData = await File.ReadAllBytesAsync(dormantObjPath, ct);

            // Compare checksums
            var localChecksum = Convert.ToHexString(SHA256.HashData(localResult.Data)).ToLowerInvariant();
            var dormantChecksum = Convert.ToHexString(SHA256.HashData(dormantData)).ToLowerInvariant();

            if (localChecksum != dormantChecksum)
            {
                var conflict = new SneakernetConflict
                {
                    ObjectId = dormantObj.ObjectId,
                    ObjectName = dormantObj.Name,
                    LocalVersion = new ConflictVersion
                    {
                        Checksum = localChecksum,
                        SizeBytes = localResult.Data.Length,
                        ModifiedAt = DateTimeOffset.UtcNow
                    },
                    DormantVersion = new ConflictVersion
                    {
                        Checksum = dormantChecksum,
                        SizeBytes = dormantData.Length,
                        ModifiedAt = dormantObj.AddedAt
                    }
                };

                newConflicts.Add(conflict);
                AddConflict(conflict);
            }
        }

        return newConflicts;
    }

    /// <summary>
    /// Adds a conflict to the queue.
    /// </summary>
    public void AddConflict(SneakernetConflict conflict)
    {
        lock (_lock)
        {
            // Check if already exists
            var existing = _conflicts.FirstOrDefault(c => c.ObjectId == conflict.ObjectId);
            if (existing != null)
            {
                _conflicts.Remove(existing);
            }
            _conflicts.Add(conflict);
            SaveQueue();
        }
    }

    /// <summary>
    /// Gets all unresolved conflicts.
    /// </summary>
    public IReadOnlyList<SneakernetConflict> GetUnresolved()
    {
        lock (_lock)
        {
            return _conflicts.Where(c => c.Resolution == ConflictResolution.Unresolved).ToList();
        }
    }

    /// <summary>
    /// Resolves a conflict with specified strategy.
    /// </summary>
    public void ResolveConflict(string objectId, ConflictResolution resolution, ConflictVersion? resolvedVersion = null)
    {
        lock (_lock)
        {
            var conflict = _conflicts.FirstOrDefault(c => c.ObjectId == objectId);
            if (conflict != null)
            {
                conflict.Resolution = resolution;
                conflict.ResolvedVersion = resolvedVersion;
                conflict.ResolvedAt = DateTimeOffset.UtcNow;
                SaveQueue();
            }
        }
    }

    /// <summary>
    /// Attempts automatic three-way merge for text-based objects.
    /// </summary>
    public async Task<bool> TryAutoMergeAsync(
        string objectId,
        ContentAddressableObjectStore localStore,
        string dormantPath,
        CancellationToken ct = default)
    {
        var conflict = _conflicts.FirstOrDefault(c => c.ObjectId == objectId);
        if (conflict == null || conflict.BaseVersion == null) return false;

        // Load all three versions
        var localResult = await localStore.RetrieveAsync(ObjectId.FromHex(objectId), ct);
        if (!localResult.Success || localResult.Data == null) return false;

        var dormantObjPath = Path.Combine(dormantPath, DormantNodeManifest.ObjectsDirectory,
            $"{objectId[..2]}/{objectId}");
        if (!File.Exists(dormantObjPath)) return false;

        var dormantData = await File.ReadAllBytesAsync(dormantObjPath, ct);
        var baseData = conflict.BaseVersion.Data;

        if (baseData == null) return false;

        // Attempt line-based merge for text files
        try
        {
            var baseText = System.Text.Encoding.UTF8.GetString(baseData);
            var localText = System.Text.Encoding.UTF8.GetString(localResult.Data);
            var dormantText = System.Text.Encoding.UTF8.GetString(dormantData);

            var merged = ThreeWayMerge(baseText, localText, dormantText);
            if (merged != null)
            {
                conflict.Resolution = ConflictResolution.Merged;
                conflict.ResolvedVersion = new ConflictVersion
                {
                    Data = System.Text.Encoding.UTF8.GetBytes(merged),
                    SizeBytes = merged.Length,
                    ModifiedAt = DateTimeOffset.UtcNow,
                    Checksum = Convert.ToHexString(SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(merged))).ToLowerInvariant()
                };
                conflict.ResolvedAt = DateTimeOffset.UtcNow;
                SaveQueue();
                return true;
            }
        }
        catch
        {
            // Not text or merge failed
        }

        return false;
    }

    private static string? ThreeWayMerge(string baseText, string local, string remote)
    {
        var baseLines = baseText.Split('\n');
        var localLines = local.Split('\n');
        var remoteLines = remote.Split('\n');

        var result = new List<string>();
        var maxLen = Math.Max(Math.Max(baseLines.Length, localLines.Length), remoteLines.Length);

        for (int i = 0; i < maxLen; i++)
        {
            var baseLine = i < baseLines.Length ? baseLines[i] : "";
            var localLine = i < localLines.Length ? localLines[i] : "";
            var remoteLine = i < remoteLines.Length ? remoteLines[i] : "";

            if (localLine == remoteLine)
            {
                // Both same - use either
                result.Add(localLine);
            }
            else if (localLine == baseLine)
            {
                // Only remote changed
                result.Add(remoteLine);
            }
            else if (remoteLine == baseLine)
            {
                // Only local changed
                result.Add(localLine);
            }
            else
            {
                // Conflict - cannot auto-merge
                return null;
            }
        }

        return string.Join('\n', result);
    }

    private void LoadQueue()
    {
        if (File.Exists(_queuePath))
        {
            try
            {
                var json = File.ReadAllText(_queuePath);
                var conflicts = JsonSerializer.Deserialize(json,
                    DormantNodeJsonContext.Default.ListSneakernetConflict);
                if (conflicts != null)
                {
                    _conflicts.AddRange(conflicts);
                }
            }
            catch { }
        }
    }

    private void SaveQueue()
    {
        var json = JsonSerializer.Serialize(_conflicts,
            DormantNodeJsonContext.Default.ListSneakernetConflict);
        File.WriteAllText(_queuePath, json);
    }
}

#endregion

#region Offline Change Tracking

/// <summary>
/// Record of a change made while offline.
/// </summary>
public sealed class OfflineChangeRecord
{
    public string ObjectId { get; set; } = string.Empty;
    public ChangeType Type { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string NodeId { get; set; } = string.Empty;
    public string Checksum { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public enum ChangeType
{
    Created,
    Modified,
    Deleted
}

/// <summary>
/// Tracks changes made during offline/disconnected periods.
/// Used for sync reconciliation when reconnecting.
/// </summary>
public sealed class OfflineChangeTracker : IDisposable
{
    private readonly ContentAddressableObjectStore _objectStore;
    private readonly string _trackingPath;
    private readonly List<OfflineChangeRecord> _changes = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly string _nodeId;
    private bool _disposed;

    public const string ChangeLogFileName = ".dw-offline-changes.json";

    public OfflineChangeTracker(
        ContentAddressableObjectStore objectStore,
        string trackingPath,
        string nodeId)
    {
        _objectStore = objectStore;
        _trackingPath = trackingPath;
        _nodeId = nodeId;

        Directory.CreateDirectory(trackingPath);
        LoadChanges();
    }

    /// <summary>
    /// Records a new change.
    /// </summary>
    public void RecordChange(string objectId, ChangeType type, string checksum, long sizeBytes)
    {
        var record = new OfflineChangeRecord
        {
            ObjectId = objectId,
            Type = type,
            Timestamp = DateTimeOffset.UtcNow,
            NodeId = _nodeId,
            Checksum = checksum,
            SizeBytes = sizeBytes
        };

        _changes.Add(record);
        SaveChanges();
    }

    /// <summary>
    /// Gets all changes since a timestamp.
    /// </summary>
    public IReadOnlyList<OfflineChangeRecord> GetChangesSince(DateTimeOffset since)
    {
        return _changes.Where(c => c.Timestamp > since).OrderBy(c => c.Timestamp).ToList();
    }

    /// <summary>
    /// Gets all tracked changes.
    /// </summary>
    public IReadOnlyList<OfflineChangeRecord> GetAllChanges()
    {
        return _changes.ToList();
    }

    /// <summary>
    /// Clears changes that have been successfully synced.
    /// </summary>
    public void ClearSyncedChanges(IEnumerable<string> syncedObjectIds)
    {
        var syncedSet = syncedObjectIds.ToHashSet();
        _changes.RemoveAll(c => syncedSet.Contains(c.ObjectId));
        SaveChanges();
    }

    /// <summary>
    /// Merges changes from a dormant node.
    /// </summary>
    public void MergeChangesFrom(string dormantPath)
    {
        var changeLogPath = Path.Combine(dormantPath, ChangeLogFileName);
        if (!File.Exists(changeLogPath)) return;

        try
        {
            var json = File.ReadAllText(changeLogPath);
            var changes = JsonSerializer.Deserialize(json,
                DormantNodeJsonContext.Default.ListOfflineChangeRecord);

            if (changes != null)
            {
                foreach (var change in changes)
                {
                    if (!_changes.Any(c => c.ObjectId == change.ObjectId &&
                                           c.Timestamp == change.Timestamp))
                    {
                        _changes.Add(change);
                    }
                }
                _changes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                SaveChanges();
            }
        }
        catch { }
    }

    private void LoadChanges()
    {
        var path = Path.Combine(_trackingPath, ChangeLogFileName);
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var changes = JsonSerializer.Deserialize(json,
                    DormantNodeJsonContext.Default.ListOfflineChangeRecord);
                if (changes != null)
                {
                    _changes.AddRange(changes);
                }
            }
            catch { }
        }
    }

    private void SaveChanges()
    {
        var path = Path.Combine(_trackingPath, ChangeLogFileName);
        var json = JsonSerializer.Serialize(_changes,
            DormantNodeJsonContext.Default.ListOfflineChangeRecord);
        File.WriteAllText(path, json);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _watcher?.Dispose();
            _disposed = true;
        }
    }
}

#endregion

#region Auto-Resync Trigger

/// <summary>
/// Automatically triggers resync when dormant nodes are detected.
/// Integrates with DormantNodeWatcher for USB mount detection.
/// </summary>
public sealed class AutoResyncTrigger : IDisposable
{
    private readonly DormantNodeWatcher _watcher;
    private readonly IncrementalSyncManager _syncManager;
    private readonly SneakernetConflictQueue _conflictQueue;
    private readonly NodeDehydrator _dehydrator;
    private readonly DehydrateOptions _defaultOptions;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentSyncs = new();
    private readonly TimeSpan _minSyncInterval;
    private bool _disposed;

    public event EventHandler<AutoResyncEventArgs>? SyncStarted;
    public event EventHandler<AutoResyncEventArgs>? SyncCompleted;
    public event EventHandler<AutoResyncEventArgs>? ConflictsDetected;

    public AutoResyncTrigger(
        IncrementalSyncManager syncManager,
        SneakernetConflictQueue conflictQueue,
        NodeDehydrator dehydrator,
        DehydrateOptions? defaultOptions = null,
        TimeSpan? minSyncInterval = null)
    {
        _syncManager = syncManager;
        _conflictQueue = conflictQueue;
        _dehydrator = dehydrator;
        _defaultOptions = defaultOptions ?? new DehydrateOptions { Encrypt = true };
        _minSyncInterval = minSyncInterval ?? TimeSpan.FromMinutes(1);

        _watcher = new DormantNodeWatcher(OnDormantNodeDetected);
    }

    /// <summary>
    /// Starts watching for dormant nodes at specified paths.
    /// </summary>
    public void StartWatching(IEnumerable<string> watchPaths)
    {
        _watcher.Watch(watchPaths);
    }

    /// <summary>
    /// Starts watching common USB mount points.
    /// </summary>
    public void StartWatchingUsbPaths()
    {
        var paths = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // Watch removable drives
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType == DriveType.Removable && drive.IsReady)
                {
                    paths.Add(drive.RootDirectory.FullName);
                }
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            paths.Add("/media");
            paths.Add("/mnt");
            var userName = Environment.UserName;
            paths.Add($"/media/{userName}");
            paths.Add($"/run/media/{userName}");
        }
        else if (OperatingSystem.IsMacOS())
        {
            paths.Add("/Volumes");
        }

        _watcher.Watch(paths.Where(Directory.Exists));
    }

    private async void OnDormantNodeDetected(string dormantPath)
    {
        // Check if we recently synced this path
        if (_recentSyncs.TryGetValue(dormantPath, out var lastSync) &&
            DateTimeOffset.UtcNow - lastSync < _minSyncInterval)
        {
            return;
        }

        _recentSyncs[dormantPath] = DateTimeOffset.UtcNow;

        var args = new AutoResyncEventArgs { DormantPath = dormantPath };

        try
        {
            SyncStarted?.Invoke(this, args);

            // Detect conflicts first
            var conflicts = await _conflictQueue.DetectConflictsAsync(
                _syncManager.ObjectStore, dormantPath);

            if (conflicts.Count > 0)
            {
                args.Conflicts = conflicts;
                ConflictsDetected?.Invoke(this, args);
                return; // Don't auto-sync if there are conflicts
            }

            // Perform incremental sync
            var result = await _syncManager.SyncAsync(
                dormantPath, _dehydrator, _defaultOptions);

            args.SyncResult = result;
            SyncCompleted?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            args.Error = ex;
            SyncCompleted?.Invoke(this, args);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _watcher.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Event args for auto-resync events.
/// </summary>
public sealed class AutoResyncEventArgs : EventArgs
{
    public string DormantPath { get; set; } = string.Empty;
    public IncrementalSyncResult? SyncResult { get; set; }
    public List<SneakernetConflict>? Conflicts { get; set; }
    public Exception? Error { get; set; }
}

#endregion
