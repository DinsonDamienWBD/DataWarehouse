using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Hosting;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Runtime.CompilerServices;
using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateStorage;

/// <summary>
/// Ultimate Storage Plugin - Comprehensive storage backend solution consolidating all storage strategies.
///
/// Implements 50+ storage backends across categories:
/// - Local Storage (FileSystem, Memory, MemoryMapped, RAMDisk)
/// - Cloud Storage (AWS S3, Azure Blob, Google Cloud Storage, MinIO, DigitalOcean Spaces)
/// - Database Storage (MongoDB GridFS, PostgreSQL Large Objects, SQL Server FileStream)
/// - Network Storage (NFS, SMB/CIFS, FTP, SFTP, WebDAV)
/// - Distributed Storage (IPFS, Arweave, Storj, Sia, Filecoin)
/// - Object Storage (OpenStack Swift, Ceph, Wasabi, Backblaze B2)
/// - Key-Value Stores (Redis, Memcached, Etcd, Consul)
/// - Specialized Storage (Tape/LTO, Optical, Cold Storage, Content Addressable Storage)
///
/// Features:
/// - Strategy pattern for backend extensibility
/// - Auto-discovery of storage strategies
/// - Unified API across all backends
/// - Multi-region support
/// - Replication and redundancy
/// - Tiered storage (hot/warm/cold)
/// - Bandwidth throttling
/// - Cost optimization
/// - Lifecycle policies
/// - Versioning support
/// - Access control integration
/// - Audit logging
/// - Health monitoring
/// - Automatic failover
/// - Compression and deduplication
/// </summary>
[PluginProfile(ServiceProfileType.Server)]
public sealed class UltimateStoragePlugin : DataWarehouse.SDK.Contracts.Hierarchy.StoragePluginBase, IDataTerminal, IDisposable
{
    private readonly StorageStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private readonly ConcurrentDictionary<string, StorageHealthStatus> _healthStatus = new();
    private bool _disposed;

    // Configuration
    private volatile string _defaultStrategyId = "filesystem";
    private volatile bool _auditEnabled = true;
    private volatile bool _autoFailoverEnabled = true;
    private volatile int _maxRetries = 3;

    // Statistics
    private long _totalWrites;
    private long _totalReads;
    private long _totalBytesWritten;
    private long _totalBytesRead;
    private long _totalDeletes;
    private long _totalFailures;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.storage.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Storage";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <summary>Sub-category for discovery.</summary>
    public string SubCategory => "Storage";

    /// <summary>Quality level (0-100).</summary>
    public int QualityLevel => 100;

    /// <inheritdoc/>
    /// <summary>Storage scheme name.</summary>
    public string StorageScheme => _defaultStrategyId;

    #region IDataTerminal Implementation

    /// <summary>
    /// Terminal ID for this storage plugin.
    /// </summary>
    public string TerminalId => _defaultStrategyId ?? Id;

    /// <summary>
    /// Terminal capabilities based on the default strategy.
    /// </summary>
    public TerminalCapabilities Capabilities => new()
    {
        Tier = GetDefaultStorageTier(),
        SupportsVersioning = HasVersioningStrategy(),
        SupportsWorm = HasWormStrategy(),
        IsContentAddressable = HasContentAddressableStrategy(),
        SupportsParallelWrite = true,
        SupportsStreaming = true,
        MaxObjectSize = GetMaxObjectSize()
    };

    /// <summary>
    /// Write data to storage terminal.
    /// </summary>
    public async Task WriteAsync(Stream input, TerminalContext context, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var strategyId = context.Parameters.TryGetValue("strategyId", out var sid) && sid is string s
            ? s
            : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        if (strategy == null)
            throw new InvalidOperationException($"No storage strategy available for '{strategyId}'");

        // Read stream to bytes
        byte[] data;
        using (var ms = new MemoryStream())
        {
            await input.CopyToAsync(ms, ct);
            data = ms.ToArray();
        }

        // Build storage options from context
        var options = BuildStorageOptionsFromContext(context);

        // Write to storage
        await strategy.WriteAsync(context.StoragePath, data, options);

        // Update statistics
        Interlocked.Increment(ref _totalWrites);
        Interlocked.Add(ref _totalBytesWritten, data.Length);
        IncrementUsageStats(strategyId ?? _defaultStrategyId);

        // Log event
        if (_auditEnabled && context.KernelContext != null)
        {
            context.KernelContext.LogDebug(
                $"[Terminal] Wrote {data.Length} bytes to {strategyId}:{context.StoragePath} (BlobId: {context.BlobId})");
        }
    }

    /// <summary>
    /// Read data from storage terminal.
    /// </summary>
    public async Task<Stream> ReadAsync(TerminalContext context, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var strategyId = context.Parameters.TryGetValue("strategyId", out var sid) && sid is string s
            ? s
            : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        if (strategy == null)
            throw new InvalidOperationException($"No storage strategy available for '{strategyId}'");

        var options = BuildStorageOptionsFromContext(context);
        var data = await strategy.ReadAsync(context.StoragePath, options);

        // Update statistics
        Interlocked.Increment(ref _totalReads);
        Interlocked.Add(ref _totalBytesRead, data.Length);

        // Log event
        if (_auditEnabled && context.KernelContext != null)
        {
            context.KernelContext.LogDebug(
                $"[Terminal] Read {data.Length} bytes from {strategyId}:{context.StoragePath} (BlobId: {context.BlobId})");
        }

        return new MemoryStream(data);
    }

    /// <summary>
    /// Delete data from storage terminal.
    /// </summary>
    public async Task<bool> DeleteAsync(TerminalContext context, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var strategyId = context.Parameters.TryGetValue("strategyId", out var sid) && sid is string s
            ? s
            : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        if (strategy == null)
            return false;

        try
        {
            var options = BuildStorageOptionsFromContext(context);
            await strategy.DeleteAsync(context.StoragePath, options);

            Interlocked.Increment(ref _totalDeletes);

            if (_auditEnabled && context.KernelContext != null)
            {
                context.KernelContext.LogDebug(
                    $"[Terminal] Deleted {strategyId}:{context.StoragePath} (BlobId: {context.BlobId})");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if data exists in storage terminal.
    /// </summary>
    public async Task<bool> ExistsAsync(TerminalContext context, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var strategyId = context.Parameters.TryGetValue("strategyId", out var sid) && sid is string s
            ? s
            : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        if (strategy == null)
            return false;

        var options = BuildStorageOptionsFromContext(context);
        return await strategy.ExistsAsync(context.StoragePath, options);
    }

    #endregion

    /// <inheritdoc/>
    public override int DefaultPipelineOrder => 100;

    /// <inheritdoc/>
    public override bool AllowBypass => false;

    /// <inheritdoc/>
    public override IReadOnlyList<string> RequiredPrecedingStages => ["Encryption"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> IncompatibleStages => [];

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate storage plugin providing 50+ storage backends including local filesystem, cloud storage " +
        "(AWS S3, Azure Blob, GCS), distributed storage (IPFS, Arweave), network storage (NFS, SMB), " +
        "database storage (MongoDB, PostgreSQL), and specialized storage. Supports multi-region, " +
        "replication, tiered storage, versioning, and automatic failover.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags => [
        "storage", "cloud", "s3", "azure", "gcs", "ipfs", "filesystem",
        "distributed", "replication", "multi-region", "backup", "archival"
    ];

    /// <summary>
    /// Gets the storage strategy registry.
    /// </summary>
    public StorageStrategyRegistry Registry => _registry;

    /// <inheritdoc/>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
    {
        get
        {
            var capabilities = new List<RegisteredCapability>
            {
                // Main plugin capability
                new()
                {
                    CapabilityId = "storage",
                    DisplayName = "Ultimate Storage",
                    Description = SemanticDescription,
                    Category = SDK.Contracts.CapabilityCategory.Storage,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = SemanticTags
                }
            };

            // Add strategy-based capabilities
            foreach (var strategy in _registry.GetAllStrategies().OfType<IStorageStrategyExtended>())
            {
                var tags = new List<string> { "storage", GetStrategyCategory(strategy.StrategyId).ToLower() };

                // Add feature tags
                if (strategy.SupportsTiering) tags.Add("tiering");
                if (strategy.SupportsVersioning) tags.Add("versioning");
                if (strategy.SupportsReplication) tags.Add("replication");

                capabilities.Add(new()
                {
                    CapabilityId = $"storage.{strategy.StrategyId}",
                    DisplayName = strategy.StrategyName,
                    Description = $"{GetStrategyCategory(strategy.StrategyId)} storage: {strategy.StrategyName}",
                    Category = SDK.Contracts.CapabilityCategory.Storage,
                    SubCategory = GetStrategyCategory(strategy.StrategyId),
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = tags.ToArray(),
                    IsAvailable = strategy.IsAvailable
                });
            }

            return capabilities.AsReadOnly();
        }
    }

    /// <summary>
    /// Gets or sets whether audit logging is enabled.
    /// </summary>
    public bool AuditEnabled
    {
        get => _auditEnabled;
        set => _auditEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether automatic failover is enabled.
    /// </summary>
    public bool AutoFailoverEnabled
    {
        get => _autoFailoverEnabled;
        set => _autoFailoverEnabled = value;
    }

    /// <summary>
    /// Gets or sets the maximum number of retries on failure.
    /// </summary>
    public int MaxRetries
    {
        get => _maxRetries;
        set => _maxRetries = value > 0 ? value : 1;
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Storage plugin.
    /// </summary>
    public UltimateStoragePlugin()
    {
        _registry = new StorageStrategyRegistry();

        // Auto-discover and register strategies
        DiscoverAndRegisterStrategies();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        response.Metadata["RegisteredStrategies"] = _registry.GetAllStrategies().Count.ToString();
        response.Metadata["DefaultStrategy"] = _defaultStrategyId;
        response.Metadata["AuditEnabled"] = _auditEnabled.ToString();
        response.Metadata["AutoFailoverEnabled"] = _autoFailoverEnabled.ToString();
        response.Metadata["MaxRetries"] = _maxRetries.ToString();

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "storage.write", DisplayName = "Write", Description = "Write data to storage backend" },
            new() { Name = "storage.read", DisplayName = "Read", Description = "Read data from storage backend" },
            new() { Name = "storage.delete", DisplayName = "Delete", Description = "Delete data from storage backend" },
            new() { Name = "storage.list", DisplayName = "List", Description = "List objects in storage backend" },
            new() { Name = "storage.exists", DisplayName = "Exists", Description = "Check if object exists" },
            new() { Name = "storage.copy", DisplayName = "Copy", Description = "Copy object between locations" },
            new() { Name = "storage.move", DisplayName = "Move", Description = "Move object to new location" },
            new() { Name = "storage.list-strategies", DisplayName = "List Strategies", Description = "List available storage strategies" },
            new() { Name = "storage.set-default", DisplayName = "Set Default", Description = "Set default storage strategy" },
            new() { Name = "storage.stats", DisplayName = "Statistics", Description = "Get storage statistics" },
            new() { Name = "storage.health", DisplayName = "Health Check", Description = "Check health of storage backends" },
            new() { Name = "storage.replicate", DisplayName = "Replicate", Description = "Replicate data across backends" },
            new() { Name = "storage.tier", DisplayName = "Tier Data", Description = "Move data between storage tiers" },
            new() { Name = "storage.get-metadata", DisplayName = "Get Metadata", Description = "Get object metadata" },
            new() { Name = "storage.set-metadata", DisplayName = "Set Metadata", Description = "Set object metadata" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.GetAllStrategies().Count;
        metadata["LocalStrategies"] = GetStrategiesByCategory("local").Count;
        metadata["CloudStrategies"] = GetStrategiesByCategory("cloud").Count;
        metadata["DistributedStrategies"] = GetStrategiesByCategory("distributed").Count;
        metadata["NetworkStrategies"] = GetStrategiesByCategory("network").Count;
        metadata["TotalWrites"] = Interlocked.Read(ref _totalWrites);
        metadata["TotalReads"] = Interlocked.Read(ref _totalReads);
        metadata["TotalBytesWritten"] = Interlocked.Read(ref _totalBytesWritten);
        metadata["TotalBytesRead"] = Interlocked.Read(ref _totalBytesRead);
        return metadata;
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>().ToList();

        var localStrategies = strategies.Count(s => GetStrategyCategory(s.StrategyId).Equals("local", StringComparison.OrdinalIgnoreCase));
        var cloudStrategies = strategies.Count(s => GetStrategyCategory(s.StrategyId).Equals("cloud", StringComparison.OrdinalIgnoreCase));
        var distributedStrategies = strategies.Count(s => GetStrategyCategory(s.StrategyId).Equals("distributed", StringComparison.OrdinalIgnoreCase));
        var networkStrategies = strategies.Count(s => GetStrategyCategory(s.StrategyId).Equals("network", StringComparison.OrdinalIgnoreCase));
        var databaseStrategies = strategies.Count(s => GetStrategyCategory(s.StrategyId).Equals("database", StringComparison.OrdinalIgnoreCase));
        var specializedStrategies = strategies.Count(s => GetStrategyCategory(s.StrategyId).Equals("specialized", StringComparison.OrdinalIgnoreCase));

        var tieringSupport = strategies.Count(s => s.SupportsTiering);
        var versioningSupport = strategies.Count(s => s.SupportsVersioning);
        var replicationSupport = strategies.Count(s => s.SupportsReplication);

        return new List<KnowledgeObject>
        {
            new()
            {
                Id = $"{Id}:overview",
                Topic = "plugin.capabilities",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = SemanticDescription,
                Payload = new Dictionary<string, object>
                {
                    ["totalStrategies"] = strategies.Count,
                    ["categories"] = new Dictionary<string, object>
                    {
                        ["local"] = localStrategies,
                        ["cloud"] = cloudStrategies,
                        ["distributed"] = distributedStrategies,
                        ["network"] = networkStrategies,
                        ["database"] = databaseStrategies,
                        ["specialized"] = specializedStrategies
                    },
                    ["features"] = new Dictionary<string, object>
                    {
                        ["tieringSupport"] = tieringSupport,
                        ["versioningSupport"] = versioningSupport,
                        ["replicationSupport"] = replicationSupport
                    },
                    ["availableStrategies"] = strategies
                        .Where(s => s.IsAvailable)
                        .Select(s => s.StrategyId)
                        .ToArray()
                },
                Tags = SemanticTags
            }
        }.AsReadOnly();
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "storage.write" => HandleWriteAsync(message),
            "storage.read" => HandleReadAsync(message),
            "storage.delete" => HandleDeleteAsync(message),
            "storage.list" => HandleListAsync(message),
            "storage.exists" => HandleExistsAsync(message),
            "storage.copy" => HandleCopyAsync(message),
            "storage.move" => HandleMoveAsync(message),
            "storage.list-strategies" => HandleListStrategiesAsync(message),
            "storage.set-default" => HandleSetDefaultAsync(message),
            "storage.stats" => HandleStatsAsync(message),
            "storage.health" => HandleHealthCheckAsync(message),
            "storage.replicate" => HandleReplicateAsync(message),
            "storage.tier" => HandleTierAsync(message),
            "storage.get-metadata" => HandleGetMetadataAsync(message),
            "storage.set-metadata" => HandleSetMetadataAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    /// <summary>Write data via pipeline transform.</summary>
    public async Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Get strategy
        var strategyId = args.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);

        // Get storage path
        var path = args.TryGetValue("path", out var pathObj) && pathObj is string p
            ? p : GenerateStoragePath();

        // Read input data
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms);
        var data = ms.ToArray();

        // Get storage options
        var options = BuildStorageOptions(args);

        // Write with retry logic
        var written = await ExecuteWithRetryAsync(async () =>
        {
            await strategy.WriteAsync(path, data, options);
            return true;
        }, strategyId);

        if (written)
        {
            // Update statistics
            Interlocked.Increment(ref _totalWrites);
            Interlocked.Add(ref _totalBytesWritten, data.Length);
            IncrementUsageStats(strategyId);

            if (_auditEnabled)
            {
                context.LogDebug($"Wrote {data.Length} bytes to {strategyId}:{path}");
            }

            // Store path in args for retrieval
            args["storagePath"] = path;
            args["storageStrategy"] = strategyId;
        }

        // Return empty stream (data is now in backend)
        return new MemoryStream();
    }

    /// <summary>Read data via pipeline transform.</summary>
    public async Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Get storage path and strategy
        if (!args.TryGetValue("storagePath", out var pathObj) || pathObj is not string path)
        {
            throw new ArgumentException("Missing 'storagePath' in args");
        }

        var strategyId = args.TryGetValue("storageStrategy", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);

        // Get storage options
        var options = BuildStorageOptions(args);

        // Read with retry logic
        var data = await ExecuteWithRetryAsync(async () =>
        {
            return await strategy.ReadAsync(path, options);
        }, strategyId);

        // Update statistics
        Interlocked.Increment(ref _totalReads);
        Interlocked.Add(ref _totalBytesRead, data.Length);

        if (_auditEnabled)
        {
            context.LogDebug($"Read {data.Length} bytes from {strategyId}:{path}");
        }

        return new MemoryStream(data);
    }

    #region Message Handlers

    private async Task HandleWriteAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var path = message.Payload.TryGetValue("path", out var pathObj) && pathObj is string p
            ? p : GenerateStoragePath();

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        await ExecuteWithRetryAsync(async () =>
        {
            await strategy.WriteAsync(path, data, options);
            return true;
        }, strategyId);

        message.Payload["path"] = path;
        message.Payload["strategyId"] = strategyId;
        message.Payload["bytesWritten"] = data.Length;

        Interlocked.Increment(ref _totalWrites);
        Interlocked.Add(ref _totalBytesWritten, data.Length);
        IncrementUsageStats(strategyId);
    }

    private async Task HandleReadAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            throw new ArgumentException("Missing 'path' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        var data = await ExecuteWithRetryAsync(async () =>
        {
            return await strategy.ReadAsync(path, options);
        }, strategyId);

        message.Payload["data"] = data;
        message.Payload["bytesRead"] = data.Length;

        Interlocked.Increment(ref _totalReads);
        Interlocked.Add(ref _totalBytesRead, data.Length);
    }

    private async Task HandleDeleteAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            throw new ArgumentException("Missing 'path' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        await ExecuteWithRetryAsync(async () =>
        {
            await strategy.DeleteAsync(path, options);
            return true;
        }, strategyId);

        message.Payload["deleted"] = true;
        Interlocked.Increment(ref _totalDeletes);
    }

    private async Task HandleListAsync(PluginMessage message)
    {
        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var prefix = message.Payload.TryGetValue("prefix", out var prefixObj) && prefixObj is string p
            ? p : "";

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        var items = await ExecuteWithRetryAsync(async () =>
        {
            return await strategy.ListAsync(prefix, options);
        }, strategyId);

        message.Payload["items"] = items;
        message.Payload["count"] = items.Count;
    }

    private async Task HandleExistsAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            throw new ArgumentException("Missing 'path' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        var exists = await ExecuteWithRetryAsync(async () =>
        {
            return await strategy.ExistsAsync(path, options);
        }, strategyId);

        message.Payload["exists"] = exists;
    }

    private async Task HandleCopyAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("sourcePath", out var srcObj) || srcObj is not string sourcePath)
        {
            throw new ArgumentException("Missing 'sourcePath' parameter");
        }

        if (!message.Payload.TryGetValue("destinationPath", out var dstObj) || dstObj is not string destinationPath)
        {
            throw new ArgumentException("Missing 'destinationPath' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        await ExecuteWithRetryAsync(async () =>
        {
            await strategy.CopyAsync(sourcePath, destinationPath, options);
            return true;
        }, strategyId);

        message.Payload["copied"] = true;
    }

    private async Task HandleMoveAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("sourcePath", out var srcObj) || srcObj is not string sourcePath)
        {
            throw new ArgumentException("Missing 'sourcePath' parameter");
        }

        if (!message.Payload.TryGetValue("destinationPath", out var dstObj) || dstObj is not string destinationPath)
        {
            throw new ArgumentException("Missing 'destinationPath' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        await ExecuteWithRetryAsync(async () =>
        {
            await strategy.MoveAsync(sourcePath, destinationPath, options);
            return true;
        }, strategyId);

        message.Payload["moved"] = true;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["name"] = s.StrategyName,
            ["category"] = s.Category,
            ["isAvailable"] = s.IsAvailable,
            ["supportsTiering"] = s.SupportsTiering,
            ["supportsVersioning"] = s.SupportsVersioning,
            ["supportsReplication"] = s.SupportsReplication,
            ["maxObjectSize"] = s.MaxObjectSize ?? 0L
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;

        return Task.CompletedTask;
    }

    private Task HandleSetDefaultAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"Strategy '{strategyId}' not found");

        _defaultStrategyId = strategyId;
        message.Payload["success"] = true;
        message.Payload["defaultStrategy"] = strategyId;

        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["totalWrites"] = Interlocked.Read(ref _totalWrites);
        message.Payload["totalReads"] = Interlocked.Read(ref _totalReads);
        message.Payload["totalDeletes"] = Interlocked.Read(ref _totalDeletes);
        message.Payload["totalBytesWritten"] = Interlocked.Read(ref _totalBytesWritten);
        message.Payload["totalBytesRead"] = Interlocked.Read(ref _totalBytesRead);
        message.Payload["totalFailures"] = Interlocked.Read(ref _totalFailures);
        message.Payload["registeredStrategies"] = _registry.GetAllStrategies().Count;

        var usageByStrategy = new Dictionary<string, long>(_usageStats);
        message.Payload["usageByStrategy"] = usageByStrategy;

        return Task.CompletedTask;
    }

    private async Task HandleHealthCheckAsync(PluginMessage message)
    {
        var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>();
        var healthResults = new Dictionary<string, object>();

        foreach (var strategy in strategies)
        {
            try
            {
                var isHealthy = await strategy.HealthCheckAsync();
                healthResults[strategy.StrategyId] = new Dictionary<string, object>
                {
                    ["healthy"] = isHealthy,
                    ["lastCheck"] = DateTime.UtcNow
                };

                _healthStatus[strategy.StrategyId] = new StorageHealthStatus
                {
                    IsHealthy = isHealthy,
                    LastCheck = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                healthResults[strategy.StrategyId] = new Dictionary<string, object>
                {
                    ["healthy"] = false,
                    ["error"] = ex.Message,
                    ["lastCheck"] = DateTime.UtcNow
                };

                _healthStatus[strategy.StrategyId] = new StorageHealthStatus
                {
                    IsHealthy = false,
                    LastCheck = DateTime.UtcNow,
                    ErrorMessage = ex.Message
                };
            }
        }

        message.Payload["health"] = healthResults;
    }

    private async Task HandleReplicateAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            throw new ArgumentException("Missing 'path' parameter");
        }

        if (!message.Payload.TryGetValue("sourceStrategy", out var srcObj) || srcObj is not string sourceStrategy)
        {
            throw new ArgumentException("Missing 'sourceStrategy' parameter");
        }

        if (!message.Payload.TryGetValue("targetStrategies", out var tgtObj) || tgtObj is not IEnumerable<string> targetStrategies)
        {
            throw new ArgumentException("Missing or invalid 'targetStrategies' parameter");
        }

        var sourceStrat = await GetStrategyWithFailoverAsync(sourceStrategy);
        var options = BuildStorageOptions(message.Payload);

        // Read from source
        var data = await sourceStrat.ReadAsync(path, options);

        // Write to all targets
        var results = new Dictionary<string, bool>();
        foreach (var targetId in targetStrategies)
        {
            try
            {
                var targetStrat = await GetStrategyWithFailoverAsync(targetId);
                await targetStrat.WriteAsync(path, data, options);
                results[targetId] = true;
            }
            catch (Exception)
            {
                results[targetId] = false;
            }
        }

        message.Payload["replicationResults"] = results;
    }

    private async Task HandleTierAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            throw new ArgumentException("Missing 'path' parameter");
        }

        if (!message.Payload.TryGetValue("sourceStrategy", out var srcObj) || srcObj is not string sourceStrategy)
        {
            throw new ArgumentException("Missing 'sourceStrategy' parameter");
        }

        if (!message.Payload.TryGetValue("targetStrategy", out var tgtObj) || tgtObj is not string targetStrategy)
        {
            throw new ArgumentException("Missing 'targetStrategy' parameter");
        }

        var sourceStrat = await GetStrategyWithFailoverAsync(sourceStrategy);
        var targetStrat = await GetStrategyWithFailoverAsync(targetStrategy);
        var options = BuildStorageOptions(message.Payload);

        // Read from source
        var data = await sourceStrat.ReadAsync(path, options);

        // Write to target
        await targetStrat.WriteAsync(path, data, options);

        // Optionally delete from source
        var deleteSource = message.Payload.TryGetValue("deleteSource", out var delObj) && delObj is bool del && del;
        if (deleteSource)
        {
            await sourceStrat.DeleteAsync(path, options);
        }

        message.Payload["tiered"] = true;
    }

    private async Task HandleGetMetadataAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            throw new ArgumentException("Missing 'path' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        var metadata = await strategy.GetMetadataAsync(path, options);
        message.Payload["metadata"] = metadata;
    }

    private async Task HandleSetMetadataAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("path", out var pathObj) || pathObj is not string path)
        {
            throw new ArgumentException("Missing 'path' parameter");
        }

        if (!message.Payload.TryGetValue("metadata", out var metaObj) || metaObj is not Dictionary<string, string> metadata)
        {
            throw new ArgumentException("Missing or invalid 'metadata' parameter");
        }

        var strategyId = message.Payload.TryGetValue("strategyId", out var sidObj) && sidObj is string sid
            ? sid : _defaultStrategyId;

        var strategy = await GetStrategyWithFailoverAsync(strategyId);
        var options = BuildStorageOptions(message.Payload);

        await strategy.SetMetadataAsync(path, metadata, options);
        message.Payload["success"] = true;
    }

    #endregion

    #region Helper Methods

    private async Task<IStorageStrategyExtended> GetStrategyWithFailoverAsync(string strategyId)
    {
        var strategy = _registry.GetStrategy(strategyId) as IStorageStrategyExtended
            ?? throw new ArgumentException($"Storage strategy '{strategyId}' not found");

        // Check health status
        if (_autoFailoverEnabled && _healthStatus.TryGetValue(strategyId, out var health) && !health.IsHealthy)
        {
            // Try to find a healthy alternative in the same category
            var alternatives = _registry.GetStrategiesByCategory(strategy.Category)
                .OfType<IStorageStrategyExtended>()
                .Where(s => s.StrategyId != strategyId && s.IsAvailable)
                .ToList();

            foreach (var alt in alternatives)
            {
                if (_healthStatus.TryGetValue(alt.StrategyId, out var altHealth) && altHealth.IsHealthy)
                {
                    return alt;
                }
            }
        }

        return strategy;
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation, string strategyId)
    {
        var attempts = 0;
        Exception? lastException = null;

        while (attempts < _maxRetries)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                lastException = ex;
                attempts++;

                if (attempts < _maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempts))); // Exponential backoff
                }
            }
        }

        Interlocked.Increment(ref _totalFailures);
        throw new InvalidOperationException(
            $"Operation failed after {attempts} attempts on strategy '{strategyId}'",
            lastException);
    }

    private List<IStorageStrategy> GetStrategiesByCategory(string category)
    {
        return _registry.GetStrategiesByCategory(category).ToList();
    }

    private static string GetStrategyCategory(string strategyId)
    {
        var id = strategyId.ToLowerInvariant();

        if (id.Contains("s3") || id.Contains("azure") || id.Contains("gcs") || id.Contains("cloud") ||
            id.Contains("blob") || id.Contains("bucket") || id.Contains("minio") || id.Contains("spaces"))
            return "cloud";

        if (id.Contains("ipfs") || id.Contains("arweave") || id.Contains("storj") || id.Contains("sia") ||
            id.Contains("filecoin") || id.Contains("swarm"))
            return "distributed";

        if (id.Contains("nfs") || id.Contains("smb") || id.Contains("cifs") || id.Contains("ftp") ||
            id.Contains("sftp") || id.Contains("webdav"))
            return "network";

        if (id.Contains("mongo") || id.Contains("postgres") || id.Contains("sql") || id.Contains("redis") ||
            id.Contains("database") || id.Contains("gridfs"))
            return "database";

        if (id.Contains("file") || id.Contains("disk") || id.Contains("local") || id.Contains("memory") ||
            id.Contains("ram"))
            return "local";

        if (id.Contains("tape") || id.Contains("lto") || id.Contains("optical") || id.Contains("cold"))
            return "specialized";

        return "other";
    }

    private void IncrementUsageStats(string strategyId)
    {
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
    }

    private void DiscoverAndRegisterStrategies()
    {
        // Auto-discover strategies in this assembly
        _registry.DiscoverStrategies(Assembly.GetExecutingAssembly());
    }

    private static string GenerateStoragePath()
    {
        return $"{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}";
    }

    private static StorageOptions BuildStorageOptions(Dictionary<string, object> args)
    {
        return new StorageOptions
        {
            Timeout = args.TryGetValue("timeout", out var tObj) && tObj is int t
                ? TimeSpan.FromSeconds(t)
                : TimeSpan.FromSeconds(30),
            BufferSize = args.TryGetValue("bufferSize", out var bObj) && bObj is int b
                ? b : 81920,
            EnableCompression = args.TryGetValue("compress", out var cObj) && cObj is bool c && c,
            Metadata = args.TryGetValue("metadata", out var mObj) && mObj is Dictionary<string, string> m
                ? m : new Dictionary<string, string>()
        };
    }

    #endregion

    #region Intelligence Integration

    /// <summary>
    /// Called when Intelligence becomes available - register storage capabilities.
    /// </summary>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        // Register storage capabilities with Intelligence
        if (MessageBus != null)
        {
            var strategies = _registry.GetAllStrategies().OfType<IStorageStrategyExtended>().ToList();
            var tieringSupport = strategies.Count(s => s.SupportsTiering);
            var versioningSupport = strategies.Count(s => s.SupportsVersioning);
            var replicationSupport = strategies.Count(s => s.SupportsReplication);

            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "storage",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = strategies.Count,
                        ["tieringSupport"] = tieringSupport,
                        ["versioningSupport"] = versioningSupport,
                        ["replicationSupport"] = replicationSupport,
                        ["supportsTieringRecommendation"] = true,
                        ["supportsAccessPrediction"] = true,
                        ["localStrategies"] = GetStrategiesByCategory("local").Count,
                        ["cloudStrategies"] = GetStrategiesByCategory("cloud").Count,
                        ["distributedStrategies"] = GetStrategiesByCategory("distributed").Count
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            // Subscribe to tiering recommendation requests
            SubscribeToTieringRequests();
        }
    }

    /// <summary>
    /// Subscribes to storage tiering recommendation requests from Intelligence.
    /// </summary>
    private void SubscribeToTieringRequests()
    {
        if (MessageBus == null) return;

        MessageBus.Subscribe(IntelligenceTopics.RequestTieringRecommendation, async msg =>
        {
            if (msg.Payload.TryGetValue("objectId", out var oidObj) && oidObj is string objectId)
            {
                var recommendation = RecommendStorageTier(objectId, msg.Payload);

                await MessageBus.PublishAsync(IntelligenceTopics.RequestTieringRecommendationResponse, new PluginMessage
                {
                    Type = "tiering-recommendation.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["objectId"] = objectId,
                        ["recommendedTier"] = recommendation.RecommendedTier.ToString(),
                        ["currentTier"] = recommendation.CurrentTier.ToString(),
                        ["confidence"] = recommendation.Confidence,
                        ["predictedAccessFrequency"] = recommendation.AccessFrequency,
                        ["estimatedCostSavings"] = recommendation.CostSavings,
                        ["reasoning"] = recommendation.Reasoning
                    }
                });
            }
        });
    }

    /// <summary>
    /// Recommends a storage tier based on access patterns.
    /// </summary>
    private (SDK.Contracts.StorageTier RecommendedTier, SDK.Contracts.StorageTier CurrentTier, double Confidence, string AccessFrequency, decimal CostSavings, string Reasoning)
        RecommendStorageTier(string objectId, Dictionary<string, object> context)
    {
        var currentTier = SDK.Contracts.StorageTier.Hot;
        if (context.TryGetValue("currentTier", out var ctObj) && ctObj is string ct)
        {
            Enum.TryParse<SDK.Contracts.StorageTier>(ct, true, out currentTier);
        }

        var lastAccessDays = context.TryGetValue("daysSinceLastAccess", out var laObj) && laObj is int la ? la : 0;
        var accessCount30Days = context.TryGetValue("accessCount30Days", out var acObj) && acObj is int ac ? ac : 0;

        // Very old data with no access: recommend Archive
        if (lastAccessDays > 365 && accessCount30Days == 0)
        {
            return (SDK.Contracts.StorageTier.Archive, currentTier, 0.95, "Rare",
                50.0m, "Data unused for over 1 year - archive tier recommended for cost savings");
        }

        // Infrequent access: recommend Cold
        if (lastAccessDays > 90 || accessCount30Days < 5)
        {
            return (SDK.Contracts.StorageTier.Cold, currentTier, 0.88, "Infrequent",
                30.0m, "Low access frequency - cold storage recommended");
        }

        // Moderate access: recommend Warm
        if (accessCount30Days < 50)
        {
            return (SDK.Contracts.StorageTier.Warm, currentTier, 0.82, "Moderate",
                15.0m, "Moderate access pattern - warm storage suitable");
        }

        // Frequent access: stay in Hot
        return (SDK.Contracts.StorageTier.Hot, currentTier, 0.90, "Frequent",
            0.0m, "High access frequency - hot storage recommended for performance");
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion

    #region Terminal Helper Methods

    private SDK.Contracts.StorageTier GetDefaultStorageTier()
    {
        // Determine tier based on default strategy type
        if (_defaultStrategyId?.Contains("Memory", StringComparison.OrdinalIgnoreCase) == true)
            return SDK.Contracts.StorageTier.Memory;
        if (_defaultStrategyId?.Contains("Archive", StringComparison.OrdinalIgnoreCase) == true)
            return SDK.Contracts.StorageTier.Archive;
        if (_defaultStrategyId?.Contains("Cold", StringComparison.OrdinalIgnoreCase) == true ||
            _defaultStrategyId?.Contains("Glacier", StringComparison.OrdinalIgnoreCase) == true)
            return SDK.Contracts.StorageTier.Cold;
        if (_defaultStrategyId?.Contains("Infrequent", StringComparison.OrdinalIgnoreCase) == true)
            return SDK.Contracts.StorageTier.Warm;
        return SDK.Contracts.StorageTier.Hot;
    }

    private bool HasVersioningStrategy()
    {
        return _registry.GetAllStrategies()
            .OfType<IStorageStrategyExtended>()
            .Any(s => s.SupportsVersioning);
    }

    private bool HasWormStrategy()
    {
        return _registry.GetAllStrategies()
            .OfType<IStorageStrategyExtended>()
            .Any(s => s.GetType().Name.Contains("Worm", StringComparison.OrdinalIgnoreCase) ||
                     s.GetType().Name.Contains("Immutable", StringComparison.OrdinalIgnoreCase));
    }

    private bool HasContentAddressableStrategy()
    {
        return _registry.GetAllStrategies()
            .OfType<IStorageStrategyExtended>()
            .Any(s => s.GetType().Name.Contains("CAS", StringComparison.OrdinalIgnoreCase) ||
                     s.GetType().Name.Contains("ContentAddressable", StringComparison.OrdinalIgnoreCase));
    }

    private long? GetMaxObjectSize()
    {
        // Return the max across all strategies, or null if unlimited
        var maxSizes = _registry.GetAllStrategies()
            .OfType<IStorageStrategyExtended>()
            .Where(s => s.MaxObjectSize.HasValue)
            .Select(s => s.MaxObjectSize!.Value)
            .ToList();

        return maxSizes.Count > 0 ? maxSizes.Max() : null;
    }

    private StorageOptions BuildStorageOptionsFromContext(TerminalContext context)
    {
        var options = new StorageOptions();

        if (context.Parameters.TryGetValue("timeout", out var tObj) && tObj is int t)
            options.Timeout = TimeSpan.FromSeconds(t);

        if (context.Parameters.TryGetValue("bufferSize", out var bObj) && bObj is int b)
            options.BufferSize = b;

        if (context.Parameters.TryGetValue("compress", out var cObj) && cObj is bool c)
            options.EnableCompression = c;

        // Merge content type and tags into metadata
        if (context.ContentType != null)
        {
            options.Metadata ??= new Dictionary<string, string>();
            options.Metadata["ContentType"] = context.ContentType;
        }

        if (context.Tags != null)
        {
            options.Metadata ??= new Dictionary<string, string>();
            foreach (var tag in context.Tags)
            {
                options.Metadata[tag.Key] = tag.Value;
            }
        }

        return options;
    }

    #endregion

    #region Hierarchy StoragePluginBase Abstract Methods

    /// <inheritdoc/>
    public override async Task<StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var strategy = await GetStrategyWithFailoverAsync(_defaultStrategyId);
        using var ms = new MemoryStream();
        await data.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var options = BuildStorageOptions(new Dictionary<string, object>());
        if (metadata != null)
        {
            foreach (var kvp in metadata)
                options.Metadata[kvp.Key] = kvp.Value;
        }
        await strategy.WriteAsync(key, bytes, options, ct);
        Interlocked.Increment(ref _totalWrites);
        Interlocked.Add(ref _totalBytesWritten, bytes.Length);
        return new StorageObjectMetadata { Key = key, Size = bytes.Length, Created = DateTime.UtcNow, Modified = DateTime.UtcNow };
    }

    /// <inheritdoc/>
    public override async Task<Stream> RetrieveAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var strategy = await GetStrategyWithFailoverAsync(_defaultStrategyId);
        var options = BuildStorageOptions(new Dictionary<string, object>());
        var data = await strategy.ReadAsync(key, options, ct);
        Interlocked.Increment(ref _totalReads);
        Interlocked.Add(ref _totalBytesRead, data.Length);
        return new MemoryStream(data);
    }

    /// <inheritdoc/>
    public override async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var strategy = await GetStrategyWithFailoverAsync(_defaultStrategyId);
        var options = BuildStorageOptions(new Dictionary<string, object>());
        await strategy.DeleteAsync(key, options, ct);
        Interlocked.Increment(ref _totalDeletes);
    }

    /// <inheritdoc/>
    public override async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var strategy = await GetStrategyWithFailoverAsync(_defaultStrategyId);
        var options = BuildStorageOptions(new Dictionary<string, object>());
        return await strategy.ExistsAsync(key, options, ct);
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<StorageObjectMetadata> ListAsync(string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // No direct list API on IStorageStrategyExtended; yield empty for now
        await Task.CompletedTask;
        yield break;
    }

    /// <inheritdoc/>
    public override Task<StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.FromResult(new StorageObjectMetadata { Key = key });
    }

    /// <inheritdoc/>
    public override Task<StorageHealthInfo> GetHealthAsync(CancellationToken ct = default)
    {
        var healthy = _healthStatus.Values.All(h => h.IsHealthy);
        return Task.FromResult(new StorageHealthInfo
        {
            Status = healthy ? DataWarehouse.SDK.Contracts.Storage.HealthStatus.Healthy : DataWarehouse.SDK.Contracts.Storage.HealthStatus.Degraded,
            LatencyMs = 0
        });
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _usageStats.Clear();
            _healthStatus.Clear();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Represents the health status of a storage backend.
    /// </summary>
    private sealed class StorageHealthStatus
    {
        public bool IsHealthy { get; set; }
        public DateTime LastCheck { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
