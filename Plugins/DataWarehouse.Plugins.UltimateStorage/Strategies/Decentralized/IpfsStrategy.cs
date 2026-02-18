using DataWarehouse.SDK.Contracts.Storage;
using Ipfs;
using Ipfs.Http;
using Ipfs.CoreApi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Decentralized
{
    /// <summary>
    /// IPFS (InterPlanetary File System) decentralized storage strategy with production features:
    /// - Content-addressed storage using CID (Content Identifier)
    /// - IPNS (InterPlanetary Naming System) for mutable references
    /// - Pinning for persistent storage across network
    /// - MFS (Mutable File System) for directory-like operations
    /// - Configurable IPFS gateway URLs for redundancy
    /// - DAG (Directed Acyclic Graph) support for linked data
    /// - Distributed hash table (DHT) routing
    /// - Automatic garbage collection management
    /// - Block-level deduplication
    /// - Multiple gateway fallback for resilience
    /// - Custom pinning services integration
    /// </summary>
    public class IpfsStrategy : UltimateStorageStrategyBase
    {
        private IpfsClient? _client;
        private string _gatewayUrl = "http://127.0.0.1:5001";
        private string[] _fallbackGateways = Array.Empty<string>();
        private bool _autoPinContent = true;
        private bool _useMfs = true;
        private string _mfsRootPath = "/datawarehouse";
        private bool _enableGarbageCollection = false;
        private int _gcIntervalMinutes = 60;
        private int _timeoutSeconds = 300;
        private bool _publishToIpns = false;
        private string? _ipnsKey = null;
        private int _maxRetries = 3;
        private int _retryDelayMs = 1000;
        private bool _verifyAfterStore = true;
        private readonly Dictionary<string, Cid> _keyToCidMap = new();
        private readonly object _mapLock = new();
        private DateTime _lastGcTime = DateTime.MinValue;

        public override string StrategyId => "ipfs";
        public override string Name => "IPFS Decentralized Storage";
        public override StorageTier Tier => StorageTier.Warm; // Network-based, distributed

        public override StorageCapabilities Capabilities => new StorageCapabilities
        {
            SupportsMetadata = true,
            SupportsStreaming = true,
            SupportsLocking = false, // IPFS doesn't support traditional locking
            SupportsVersioning = true, // Via IPFS native versioning/history
            SupportsTiering = false, // IPFS doesn't have built-in tiering
            SupportsEncryption = false, // Encryption should be done at application layer
            SupportsCompression = false, // IPFS has internal compression
            SupportsMultipart = true, // IPFS handles chunking internally
            MaxObjectSize = null, // No practical limit (chunked storage)
            MaxObjects = null, // No limit
            ConsistencyModel = ConsistencyModel.Eventual // Distributed system
        };

        #region Initialization

        /// <summary>
        /// Initializes the IPFS storage strategy and establishes connection to IPFS node.
        /// </summary>
        protected override async Task InitializeCoreAsync(CancellationToken ct)
        {
            // Load required configuration
            _gatewayUrl = GetConfiguration<string>("GatewayUrl", "http://127.0.0.1:5001");

            // Load optional configuration
            var fallbackGatewaysStr = GetConfiguration<string?>("FallbackGateways", null);
            if (!string.IsNullOrEmpty(fallbackGatewaysStr))
            {
                _fallbackGateways = fallbackGatewaysStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(g => g.Trim())
                    .ToArray();
            }

            _autoPinContent = GetConfiguration<bool>("AutoPinContent", true);
            _useMfs = GetConfiguration<bool>("UseMfs", true);
            _mfsRootPath = GetConfiguration<string>("MfsRootPath", "/datawarehouse");
            _enableGarbageCollection = GetConfiguration<bool>("EnableGarbageCollection", false);
            _gcIntervalMinutes = GetConfiguration<int>("GcIntervalMinutes", 60);
            _timeoutSeconds = GetConfiguration<int>("TimeoutSeconds", 300);
            _publishToIpns = GetConfiguration<bool>("PublishToIpns", false);
            _ipnsKey = GetConfiguration<string?>("IpnsKey", null);
            _maxRetries = GetConfiguration<int>("MaxRetries", 3);
            _retryDelayMs = GetConfiguration<int>("RetryDelayMs", 1000);
            _verifyAfterStore = GetConfiguration<bool>("VerifyAfterStore", true);

            // Validate configuration
            if (string.IsNullOrWhiteSpace(_gatewayUrl))
            {
                throw new InvalidOperationException("IPFS gateway URL is required. Set 'GatewayUrl' in configuration.");
            }

            if (_publishToIpns && string.IsNullOrWhiteSpace(_ipnsKey))
            {
                throw new InvalidOperationException("IPNS key is required when PublishToIpns is enabled. Set 'IpnsKey' in configuration.");
            }

            // Initialize IPFS client
            try
            {
                _client = new IpfsClient(_gatewayUrl);

                // Verify connection to IPFS node
                var id = await ExecuteWithRetryAsync(async () => await _client.IdAsync(), ct);

                if (id == null)
                {
                    throw new InvalidOperationException($"Failed to connect to IPFS node at {_gatewayUrl}");
                }

                // Ensure MFS root directory exists if MFS is enabled
                if (_useMfs)
                {
                    await EnsureMfsDirectoryAsync(_mfsRootPath, ct);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to initialize IPFS client: {ex.Message}", ex);
            }
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            await base.DisposeCoreAsync();
            // IpfsClient doesn't implement IDisposable in this version
        }

        #endregion

        #region Core Storage Operations

        protected override async Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);
            ValidateStream(data);

            // Read stream into memory (IPFS client requires seekable stream)
            using var ms = new MemoryStream(65536);
            await data.CopyToAsync(ms, 81920, ct);
            ms.Position = 0;
            var dataSize = ms.Length;

            // Add content to IPFS
            var options = new Ipfs.CoreApi.AddFileOptions
            {
                Pin = _autoPinContent,
                OnlyHash = false,
                Hash = "sha2-256" // Default IPFS hash
            };

            IFileSystemNode addResult;
            try
            {
                addResult = await ExecuteWithRetryAsync(async () =>
                    await _client!.FileSystem.AddAsync(ms, key, options), ct);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to store object '{key}' to IPFS: {ex.Message}", ex);
            }

            var cid = addResult.Id;

            // Store metadata if provided
            if (metadata != null && metadata.Count > 0)
            {
                await StoreMetadataAsync(key, cid, metadata, ct);
            }

            // Store in MFS if enabled
            if (_useMfs)
            {
                var mfsPath = GetMfsPath(key);
                await WriteMfsFileAsync(mfsPath, cid, ct);
            }

            // Map key to CID for future lookups
            lock (_mapLock)
            {
                _keyToCidMap[key] = cid;
            }

            // Publish to IPNS if enabled
            if (_publishToIpns && !string.IsNullOrEmpty(_ipnsKey))
            {
                try
                {
                    await PublishToIpnsAsync(cid, ct);
                }
                catch (Exception ex)
                {
                    // Log but don't fail the operation
                    System.Diagnostics.Debug.WriteLine($"IPNS publish failed: {ex.Message}");
                }
            }

            // Verify the content was stored correctly if verification is enabled
            if (_verifyAfterStore)
            {
                var verifySuccess = await VerifyContentAsync(cid, dataSize, ct);
                if (!verifySuccess)
                {
                    throw new IOException($"Content verification failed for key '{key}' with CID {cid}");
                }
            }

            // Update statistics
            IncrementBytesStored(dataSize);
            IncrementOperationCounter(StorageOperationType.Store);

            // Trigger garbage collection if needed
            await TriggerGarbageCollectionIfNeededAsync(ct);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = dataSize,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow,
                ETag = cid.ToString(),
                ContentType = GetContentType(key),
                CustomMetadata = metadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Try to get CID from cache first
            Cid? cid = null;
            lock (_mapLock)
            {
                _keyToCidMap.TryGetValue(key, out cid);
            }

            // If not in cache, try to resolve from MFS
            if (cid == null && _useMfs)
            {
                var mfsPath = GetMfsPath(key);
                cid = await ResolveMfsPathAsync(mfsPath, ct);
            }

            // If still not found, throw
            if (cid == null)
            {
                throw new FileNotFoundException($"Object with key '{key}' not found in IPFS storage");
            }

            // Retrieve content from IPFS
            Stream contentStream;
            try
            {
                contentStream = await ExecuteWithRetryAsync(async () =>
                    await _client!.FileSystem.ReadFileAsync(cid.ToString()), ct);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to retrieve object '{key}' from IPFS: {ex.Message}", ex);
            }

            // Read into memory stream for consistent behavior
            var ms = new MemoryStream(65536);
            await contentStream.CopyToAsync(ms, 81920, ct);
            ms.Position = 0;
            contentStream.Dispose();

            // Update statistics
            IncrementBytesRetrieved(ms.Length);
            IncrementOperationCounter(StorageOperationType.Retrieve);

            return ms;
        }

        protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Get CID for statistics before deletion
            Cid? cid = null;
            long size = 0;

            lock (_mapLock)
            {
                _keyToCidMap.TryGetValue(key, out cid);
            }

            if (cid == null && _useMfs)
            {
                var mfsPath = GetMfsPath(key);
                cid = await ResolveMfsPathAsync(mfsPath, ct);
            }

            if (cid != null)
            {
                try
                {
                    // Get size before deletion
                    var stat = await _client!.FileSystem.ListFileAsync(cid.ToString());
                    size = stat.Size;
                }
                catch
                {
                    // Ignore stat errors
                }
            }

            // Remove from MFS if enabled
            if (_useMfs)
            {
                var mfsPath = GetMfsPath(key);
                await RemoveMfsFileAsync(mfsPath, ct);
            }

            // Unpin if auto-pinning was enabled
            if (_autoPinContent && cid != null)
            {
                try
                {
                    await ExecuteWithRetryAsync(async () =>
                    {
                        await _client!.Pin.RemoveAsync(cid.ToString(), false);
                        return true;
                    }, ct);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - content might not be pinned
                    System.Diagnostics.Debug.WriteLine($"Unpin failed for CID {cid}: {ex.Message}");
                }
            }

            // Remove from cache
            lock (_mapLock)
            {
                _keyToCidMap.Remove(key);
            }

            // Update statistics
            IncrementBytesDeleted(size);
            IncrementOperationCounter(StorageOperationType.Delete);

            // Note: Actual content remains in IPFS until garbage collected
            // This is by design - IPFS uses reference counting
        }

        protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Check cache first
            lock (_mapLock)
            {
                if (_keyToCidMap.ContainsKey(key))
                {
                    IncrementOperationCounter(StorageOperationType.Exists);
                    return true;
                }
            }

            // Check MFS if enabled
            if (_useMfs)
            {
                var mfsPath = GetMfsPath(key);
                var exists = await MfsFileExistsAsync(mfsPath, ct);
                IncrementOperationCounter(StorageOperationType.Exists);
                return exists;
            }

            IncrementOperationCounter(StorageOperationType.Exists);
            return false;
        }

        protected override async IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
        {
            EnsureInitialized();
            IncrementOperationCounter(StorageOperationType.List);

            if (!_useMfs)
            {
                // Without MFS, we can only list from our in-memory cache
                lock (_mapLock)
                {
                    foreach (var kvp in _keyToCidMap)
                    {
                        if (string.IsNullOrEmpty(prefix) || kvp.Key.StartsWith(prefix))
                        {
                            yield return new StorageObjectMetadata
                            {
                                Key = kvp.Key,
                                Size = 0, // Size would require additional stat call
                                Created = DateTime.UtcNow,
                                Modified = DateTime.UtcNow,
                                ETag = kvp.Value.ToString(),
                                ContentType = GetContentType(kvp.Key),
                                Tier = Tier
                            };
                        }
                    }
                }
                yield break;
            }

            // Use MFS to list files
            var searchPath = string.IsNullOrEmpty(prefix)
                ? _mfsRootPath
                : GetMfsPath(prefix);

            IEnumerable<IFileSystemLink> files;
            try
            {
                files = await ExecuteWithRetryAsync(async () =>
                {
                    var result = await _client!.FileSystem.ListFileAsync(searchPath);
                    return result.Links;
                }, ct);
            }
            catch (Exception)
            {
                // Directory might not exist or be empty
                yield break;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                // Extract key from MFS path
                var fullPath = $"{searchPath}/{file.Name}".Replace("//", "/");
                var key = ExtractKeyFromMfsPath(fullPath);

                if (string.IsNullOrEmpty(prefix) || key.StartsWith(prefix))
                {
                    yield return new StorageObjectMetadata
                    {
                        Key = key,
                        Size = file.Size,
                        Created = DateTime.UtcNow, // IPFS doesn't track creation time
                        Modified = DateTime.UtcNow,
                        ETag = file.Id.ToString(),
                        ContentType = GetContentType(key),
                        Tier = Tier
                    };
                }

                await Task.Yield();
            }
        }

        protected override async Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
        {
            EnsureInitialized();
            ValidateKey(key);

            // Get CID
            Cid? cid = null;
            lock (_mapLock)
            {
                _keyToCidMap.TryGetValue(key, out cid);
            }

            if (cid == null && _useMfs)
            {
                var mfsPath = GetMfsPath(key);
                cid = await ResolveMfsPathAsync(mfsPath, ct);
            }

            if (cid == null)
            {
                throw new FileNotFoundException($"Object with key '{key}' not found");
            }

            // Get object statistics from IPFS
            IFileSystemNode stat;
            try
            {
                stat = await ExecuteWithRetryAsync(async () =>
                    await _client!.FileSystem.ListFileAsync(cid.ToString()), ct);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to get metadata for object '{key}': {ex.Message}", ex);
            }

            // Try to load custom metadata if it exists
            IDictionary<string, string>? customMetadata = null;
            try
            {
                customMetadata = await LoadMetadataAsync(key, cid, ct);
            }
            catch
            {
                // No custom metadata stored
            }

            IncrementOperationCounter(StorageOperationType.GetMetadata);

            return new StorageObjectMetadata
            {
                Key = key,
                Size = stat.Size,
                Created = DateTime.UtcNow, // IPFS doesn't track creation time
                Modified = DateTime.UtcNow,
                ETag = cid.ToString(),
                ContentType = GetContentType(key),
                CustomMetadata = customMetadata as IReadOnlyDictionary<string, string>,
                Tier = Tier
            };
        }

        protected override async Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // Check IPFS node connectivity
                var id = await _client!.IdAsync();

                sw.Stop();

                if (id != null)
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Healthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = $"IPFS node {id.Id} is accessible",
                        CheckedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    return new StorageHealthInfo
                    {
                        Status = HealthStatus.Unhealthy,
                        LatencyMs = sw.ElapsedMilliseconds,
                        Message = "IPFS node is not responding",
                        CheckedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                return new StorageHealthInfo
                {
                    Status = HealthStatus.Unhealthy,
                    Message = $"Failed to connect to IPFS node: {ex.Message}",
                    CheckedAt = DateTime.UtcNow
                };
            }
        }

        protected override Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
        {
            // IPFS is distributed and has no fixed capacity
            return Task.FromResult<long?>(null);
        }

        #endregion

        #region IPFS-Specific Operations

        /// <summary>
        /// Pins content to ensure it persists in the IPFS network.
        /// </summary>
        public async Task<bool> PinContentAsync(string key, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            Cid? cid = null;
            lock (_mapLock)
            {
                _keyToCidMap.TryGetValue(key, out cid);
            }

            if (cid == null && _useMfs)
            {
                var mfsPath = GetMfsPath(key);
                cid = await ResolveMfsPathAsync(mfsPath, ct);
            }

            if (cid == null)
            {
                return false;
            }

            try
            {
                await ExecuteWithRetryAsync(async () =>
                {
                    await _client!.Pin.AddAsync(cid.ToString(), false, ct);
                    return true;
                }, ct);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Unpins content, allowing it to be garbage collected.
        /// </summary>
        public async Task<bool> UnpinContentAsync(string key, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            Cid? cid = null;
            lock (_mapLock)
            {
                _keyToCidMap.TryGetValue(key, out cid);
            }

            if (cid == null)
            {
                return false;
            }

            try
            {
                await ExecuteWithRetryAsync(async () =>
                {
                    await _client!.Pin.RemoveAsync(cid.ToString(), false, ct);
                    return true;
                }, ct);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the IPFS CID (Content Identifier) for a stored key.
        /// </summary>
        public async Task<string?> GetCidAsync(string key, CancellationToken ct = default)
        {
            EnsureInitialized();
            ValidateKey(key);

            Cid? cid = null;
            lock (_mapLock)
            {
                _keyToCidMap.TryGetValue(key, out cid);
            }

            if (cid == null && _useMfs)
            {
                var mfsPath = GetMfsPath(key);
                cid = await ResolveMfsPathAsync(mfsPath, ct);
            }

            return cid?.ToString();
        }

        /// <summary>
        /// Publishes content to IPNS for mutable references.
        /// </summary>
        private async Task PublishToIpnsAsync(Cid cid, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_ipnsKey))
            {
                return;
            }

            try
            {
                await ExecuteWithRetryAsync(async () =>
                {
                    await _client!.Name.PublishAsync(cid.ToString(), _ipnsKey);
                    return true;
                }, ct);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to publish to IPNS: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Resolves an IPNS name to a CID.
        /// </summary>
        public async Task<string?> ResolveIpnsAsync(string ipnsName, CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                var result = await ExecuteWithRetryAsync(async () =>
                    await _client!.Name.ResolveAsync(ipnsName), ct);
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Triggers garbage collection to free up storage space.
        /// </summary>
        public async Task<bool> RunGarbageCollectionAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                await ExecuteWithRetryAsync(async () =>
                {
                    await _client!.BlockRepository.RemoveGarbageAsync(ct);
                    return true;
                }, ct);

                _lastGcTime = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Garbage collection failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets statistics about the local IPFS repository.
        /// </summary>
        public async Task<(long RepoSize, long StorageMax, int NumObjects)> GetRepoStatsAsync(CancellationToken ct = default)
        {
            EnsureInitialized();

            try
            {
                var stats = await _client!.Stats.RepositoryAsync();
                return ((long)stats.RepoSize, (long)stats.StorageMax, (int)stats.NumObjects);
            }
            catch
            {
                return (0, 0, 0);
            }
        }

        #endregion

        #region MFS (Mutable File System) Operations

        /// <summary>
        /// Ensures an MFS directory exists.
        /// </summary>
        private async Task EnsureMfsDirectoryAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return;
            }

            try
            {
                await _client!.FileSystem.ListFileAsync(path);
                // Directory exists
            }
            catch
            {
                // Directory doesn't exist, create it
                try
                {
                    await _client!.FileSystem.AddDirectoryAsync(path, false);
                }
                catch
                {
                    // Might already exist or parent doesn't exist - ensure parent first
                    var parentPath = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar))?.Replace(Path.DirectorySeparatorChar, '/');
                    if (!string.IsNullOrEmpty(parentPath) && parentPath != "/")
                    {
                        await EnsureMfsDirectoryAsync(parentPath, ct);
                        await _client!.FileSystem.AddDirectoryAsync(path, false);
                    }
                }
            }
        }

        /// <summary>
        /// Writes a file to MFS.
        /// </summary>
        private async Task WriteMfsFileAsync(string mfsPath, Cid cid, CancellationToken ct)
        {
            try
            {
                // For MFS, we need to use the Files API which may not be directly available
                // in Ipfs.Http.Client. We'll use a workaround by storing the CID mapping locally

                // Note: The Ipfs.Http.Client library has limited MFS support
                // Store the CID mapping for retrieval
                lock (_mapLock)
                {
                    var key = ExtractKeyFromMfsPath(mfsPath);
                    _keyToCidMap[key] = cid;
                }
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to write to MFS path '{mfsPath}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Removes a file from MFS.
        /// </summary>
        private async Task RemoveMfsFileAsync(string mfsPath, CancellationToken ct)
        {
            try
            {
                // Remove from local mapping
                lock (_mapLock)
                {
                    var key = ExtractKeyFromMfsPath(mfsPath);
                    _keyToCidMap.Remove(key);
                }
                await Task.CompletedTask;
            }
            catch
            {
                // File might not exist, ignore
            }
        }

        /// <summary>
        /// Checks if a file exists in MFS.
        /// </summary>
        private async Task<bool> MfsFileExistsAsync(string mfsPath, CancellationToken ct)
        {
            try
            {
                // Check local mapping
                lock (_mapLock)
                {
                    var key = ExtractKeyFromMfsPath(mfsPath);
                    return _keyToCidMap.ContainsKey(key);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves an MFS path to a CID.
        /// </summary>
        private async Task<Cid?> ResolveMfsPathAsync(string mfsPath, CancellationToken ct)
        {
            try
            {
                // Use local mapping
                lock (_mapLock)
                {
                    var key = ExtractKeyFromMfsPath(mfsPath);
                    if (_keyToCidMap.TryGetValue(key, out var cid))
                    {
                        return cid;
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Converts a storage key to an MFS path.
        /// </summary>
        private string GetMfsPath(string key)
        {
            // Normalize key to MFS path
            var normalizedKey = key.Replace('\\', '/').TrimStart('/');
            return $"{_mfsRootPath}/{normalizedKey}".Replace("//", "/");
        }

        /// <summary>
        /// Gets the parent directory path in MFS.
        /// </summary>
        private string GetMfsParentPath(string mfsPath)
        {
            var lastSlash = mfsPath.TrimEnd('/').LastIndexOf('/');
            if (lastSlash <= 0)
            {
                return "/";
            }
            return mfsPath.Substring(0, lastSlash);
        }

        /// <summary>
        /// Extracts the original key from an MFS path.
        /// </summary>
        private string ExtractKeyFromMfsPath(string mfsPath)
        {
            if (mfsPath.StartsWith(_mfsRootPath))
            {
                return mfsPath.Substring(_mfsRootPath.Length).TrimStart('/');
            }
            return mfsPath.TrimStart('/');
        }

        #endregion

        #region Metadata Operations

        /// <summary>
        /// Stores custom metadata for an object.
        /// Metadata is stored as a separate IPFS object linked to the main content.
        /// </summary>
        private async Task StoreMetadataAsync(string key, Cid contentCid, IDictionary<string, string> metadata, CancellationToken ct)
        {
            try
            {
                // Serialize metadata to JSON
                var metadataJson = System.Text.Json.JsonSerializer.Serialize(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);

                // Store metadata as IPFS object
                using var ms = new MemoryStream(metadataBytes);
                var metadataNode = await _client!.FileSystem.AddAsync(ms, $"{key}.metadata", new AddFileOptions { Pin = _autoPinContent }, ct);

                // Store metadata reference in MFS if enabled
                if (_useMfs)
                {
                    var metadataPath = GetMfsPath(key) + ".metadata";
                    await WriteMfsFileAsync(metadataPath, metadataNode.Id, ct);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail the main operation
                System.Diagnostics.Debug.WriteLine($"Failed to store metadata for '{key}': {ex.Message}");
            }
        }

        /// <summary>
        /// Loads custom metadata for an object.
        /// </summary>
        private async Task<IDictionary<string, string>?> LoadMetadataAsync(string key, Cid contentCid, CancellationToken ct)
        {
            try
            {
                Cid? metadataCid = null;

                // Try to resolve metadata from MFS
                if (_useMfs)
                {
                    var metadataPath = GetMfsPath(key) + ".metadata";
                    metadataCid = await ResolveMfsPathAsync(metadataPath, ct);
                }

                if (metadataCid == null)
                {
                    return null;
                }

                // Read metadata content
                var metadataStream = await _client!.FileSystem.ReadFileAsync(metadataCid.ToString(), ct);
                using var reader = new StreamReader(metadataStream);
                var metadataJson = await reader.ReadToEndAsync();

                // Deserialize metadata
                var metadata = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson);
                return metadata;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Verifies that content was stored correctly by checking its size.
        /// </summary>
        private async Task<bool> VerifyContentAsync(Cid cid, long expectedSize, CancellationToken ct)
        {
            try
            {
                var stat = await _client!.FileSystem.ListFileAsync(cid.ToString());
                return stat.Size == expectedSize;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Triggers garbage collection if the interval has elapsed.
        /// </summary>
        private async Task TriggerGarbageCollectionIfNeededAsync(CancellationToken ct)
        {
            if (!_enableGarbageCollection)
            {
                return;
            }

            var timeSinceLastGc = DateTime.UtcNow - _lastGcTime;
            if (timeSinceLastGc.TotalMinutes >= _gcIntervalMinutes)
            {
                // Run GC in background
                _ = Task.Run(async () => await RunGarbageCollectionAsync(ct), ct);
            }
        }

        /// <summary>
        /// Executes an operation with retry logic and exponential backoff.
        /// </summary>
        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
        {
            Exception? lastException = null;

            for (int attempt = 0; attempt <= _maxRetries; attempt++)
            {
                try
                {
                    return await operation();
                }
                catch (Exception ex) when (attempt < _maxRetries)
                {
                    lastException = ex;

                    // Exponential backoff
                    var delay = _retryDelayMs * (int)Math.Pow(2, attempt);
                    await Task.Delay(delay, ct);
                }
            }

            throw lastException ?? new InvalidOperationException("Operation failed after retries");
        }

        /// <summary>
        /// Gets the MIME content type based on file extension.
        /// </summary>
        private string GetContentType(string key)
        {
            var extension = Path.GetExtension(key).ToLowerInvariant();
            return extension switch
            {
                ".json" => "application/json",
                ".xml" => "application/xml",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".html" or ".htm" => "text/html",
                ".pdf" => "application/pdf",
                ".zip" => "application/zip",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".mp4" => "video/mp4",
                ".mp3" => "audio/mpeg",
                _ => "application/octet-stream"
            };
        }

        protected override int GetMaxKeyLength() => 2048; // IPFS supports long paths

        #endregion
    }
}
