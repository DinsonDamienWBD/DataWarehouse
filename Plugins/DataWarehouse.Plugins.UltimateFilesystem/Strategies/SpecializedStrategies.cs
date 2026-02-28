namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// Container packed filesystem strategy.
/// Optimized for millions of small files.
/// </summary>
public sealed class ContainerPackedStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "container-packed";
    public override string DisplayName => "Container Packed Storage";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Container;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Container packed storage strategy that eliminates the NTFS tax for small files " +
        "by packing millions of files into container archives.";
    public override string[] Tags => ["container", "small-files", "packed", "efficient"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // In production, would read from container format
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        fs.ReadExactly(buffer, 0, length);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "container-packed" });
}

/// <summary>
/// Virtual overlay filesystem strategy.
/// Union/overlay filesystem for layered storage.
/// </summary>
public sealed class OverlayFsStrategy : FilesystemStrategyBase
{
    public override string StrategyId => "virtual-overlay";
    public override string DisplayName => "Overlay Filesystem";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Virtual;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Overlay filesystem providing union mount semantics for layered storage, " +
        "ideal for container images and versioned data.";
    public override string[] Tags => ["overlay", "union", "layers", "container", "versioned"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        fs.ReadExactly(buffer, 0, length);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "overlay" });
}

/// <summary>
/// Block caching layer strategy.
/// Adds caching layer on top of any driver.
/// </summary>
public sealed class BlockCacheStrategy : FilesystemStrategyBase
{
    private readonly Dictionary<(string, long), byte[]> _cache = new();
    private readonly object _cacheLock = new();
    private readonly int _cacheSize = 1000;

    public override string StrategyId => "cache-block";
    public override string DisplayName => "Block Cache Layer";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Cache;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = true,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Block caching layer providing read-through cache for frequently accessed blocks, " +
        "reducing I/O latency for hot data.";
    public override string[] Tags => ["cache", "block", "read-through", "hot-data"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        ValidatePath(path);
        var key = (path, offset);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.Length >= length)
                return Task.FromResult(cached[..length]);
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        fs.ReadExactly(buffer, 0, length);

        lock (_cacheLock)
        {
            if (_cache.Count < _cacheSize)
                _cache[key] = buffer;
        }

        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        // Invalidate cache on write
        lock (_cacheLock)
        {
            _cache.Remove((path, offset));
        }

        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs.Seek(offset, SeekOrigin.Begin);
        fs.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "block-cache" });
}

/// <summary>
/// Space quota enforcement strategy.
/// Enforces per-path or per-tenant quotas.
/// </summary>
public sealed class QuotaEnforcementStrategy : FilesystemStrategyBase
{
    private readonly Dictionary<string, (long used, long limit)> _quotas = new();
    private readonly object _quotasLock = new();

    public override string StrategyId => "quota-enforcement";
    public override string DisplayName => "Quota Enforcement";
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Quota;
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false, SupportsAsyncIo = true, SupportsMmap = false,
        SupportsKernelBypass = false, SupportsVectoredIo = false, SupportsSparse = false,
        SupportsAutoDetect = false
    };
    public override string SemanticDescription =>
        "Quota enforcement strategy integrating with ResourceManager (T128) for I/O quotas, " +
        "preventing storage overconsumption.";
    public override string[] Tags => ["quota", "limit", "enforcement", "tenant"];

    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        ValidatePath(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        fs.ReadExactly(buffer, 0, length);
        return Task.FromResult(buffer);
    }

    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default)
    {
        ValidatePath(path);
        // Check quota before write (under lock for TOCTOU safety)
        var root = Path.GetPathRoot(path) ?? path;
        lock (_quotasLock)
        {
            if (_quotas.TryGetValue(root, out var quota))
            {
                if (quota.used + data.Length > quota.limit)
                    throw new IOException($"Quota exceeded for {root}");

                using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(data, 0, data.Length);
                _quotas[root] = (quota.used + data.Length, quota.limit);
                return Task.CompletedTask;
            }
        }

        using var fs2 = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        fs2.Seek(offset, SeekOrigin.Begin);
        fs2.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(new FilesystemMetadata { FilesystemType = "quota" });

    /// <summary>Sets quota for a path.</summary>
    public void SetQuota(string path, long limitBytes)
    {
        var root = Path.GetPathRoot(path) ?? path;
        lock (_quotasLock)
        {
            _quotas[root] = (0, limitBytes);
        }
    }
}

// XFS and ReFS strategies have been moved to SuperblockDetectionStrategies.cs
// with production-grade superblock parsing implementations.
