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
internal partial class DormantNodeJsonContext : JsonSerializerContext
{
}
