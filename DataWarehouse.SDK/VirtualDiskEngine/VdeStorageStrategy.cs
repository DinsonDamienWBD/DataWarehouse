using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine;

/// <summary>
/// Storage strategy implementation that registers VDE as an UltimateStorage backend.
/// Delegates all operations to <see cref="VirtualDiskEngine"/>.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE storage strategy integration (VDE-07)")]
public sealed class VdeStorageStrategy : Contracts.Storage.StorageStrategyBase
{
    private VirtualDiskEngine? _engine;
    private int _blockSize = 4096; // Will be set from options

    /// <inheritdoc/>
    public override string StrategyId => "vde-container";

    /// <inheritdoc/>
    public override string Name => "Virtual Disk Engine";

    /// <inheritdoc/>
    public override string Description => "Block-level storage engine with WAL, CoW snapshots, B-Tree indexing, and checksumming";

    /// <inheritdoc/>
    public override Contracts.Storage.StorageTier Tier => Contracts.Storage.StorageTier.Hot;

    /// <inheritdoc/>
    public override Contracts.Storage.StorageCapabilities Capabilities => new()
    {
        SupportsVersioning = true,  // Via snapshots
        SupportsMetadata = true,    // Via extended attributes
        SupportsLocking = true,     // WAL provides transaction isolation
        SupportsTiering = false,
        SupportsEncryption = false,
        SupportsCompression = false,
        SupportsStreaming = true,
        SupportsMultipart = false,
        ConsistencyModel = Contracts.Storage.ConsistencyModel.Strong  // WAL provides strong consistency
    };

    private readonly Dictionary<string, object> _configuration = new();

    /// <summary>
    /// Sets configuration values for VDE initialization.
    /// </summary>
    public void SetConfiguration(string key, object value)
    {
        _configuration[key] = value;
    }

    /// <inheritdoc/>
    protected override async Task InitializeAsyncCore(CancellationToken ct = default)
    {
        // Read VdeOptions from strategy configuration (or use defaults)
        // For now, use default options with a container path in the working directory
        var options = new VdeOptions
        {
            ContainerPath = EnsureAbsoluteContainerPath(GetConfigValue<string>("ContainerPath") ?? "datawarehouse.dwvd"),
            BlockSize = GetConfigValue<int>("BlockSize", 4096),
            TotalBlocks = GetConfigValue<long>("TotalBlocks", 1_048_576),
            WalSizePercent = GetConfigValue<int>("WalSizePercent", 1),
            MaxCachedInodes = GetConfigValue<int>("MaxCachedInodes", 10_000),
            MaxCachedBTreeNodes = GetConfigValue<int>("MaxCachedBTreeNodes", 1_000),
            MaxCachedChecksumBlocks = GetConfigValue<int>("MaxCachedChecksumBlocks", 256),
            CheckpointWalUtilizationPercent = GetConfigValue<int>("CheckpointWalUtilizationPercent", 75),
            EnableChecksumVerification = GetConfigValue<bool>("EnableChecksumVerification", true),
            AutoCreateContainer = GetConfigValue<bool>("AutoCreateContainer", true)
        };

        _blockSize = options.BlockSize;

        // Create and initialize the engine
        _engine = new VirtualDiskEngine(options);
        await _engine.InitializeAsync(ct);
    }

    /// <inheritdoc/>
    protected override async Task<Contracts.Storage.StorageObjectMetadata> StoreAsyncCore(string key, System.IO.Stream data, IDictionary<string, string>? metadata, CancellationToken ct)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("VdeStorageStrategy is not initialized.");
        }

        return await _engine.StoreAsync(key, data, metadata, ct);
    }

    /// <inheritdoc/>
    protected override async Task<System.IO.Stream> RetrieveAsyncCore(string key, CancellationToken ct)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("VdeStorageStrategy is not initialized.");
        }

        return await _engine.RetrieveAsync(key, ct);
    }

    /// <inheritdoc/>
    protected override async Task DeleteAsyncCore(string key, CancellationToken ct)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("VdeStorageStrategy is not initialized.");
        }

        await _engine.DeleteAsync(key, ct);
    }

    /// <inheritdoc/>
    protected override async Task<bool> ExistsAsyncCore(string key, CancellationToken ct)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("VdeStorageStrategy is not initialized.");
        }

        return await _engine.ExistsAsync(key, ct);
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<Contracts.Storage.StorageObjectMetadata> ListAsyncCore(string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("VdeStorageStrategy is not initialized.");
        }

        await foreach (var item in _engine.ListAsync(prefix, ct))
        {
            yield return item;
        }
    }

    /// <inheritdoc/>
    protected override async Task<Contracts.Storage.StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("VdeStorageStrategy is not initialized.");
        }

        return await _engine.GetMetadataAsync(key, ct);
    }

    /// <inheritdoc/>
    protected override async Task<Contracts.Storage.StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("VdeStorageStrategy is not initialized.");
        }

        var vdeHealth = await _engine.GetHealthReportAsync(ct);
        return vdeHealth.ToStorageHealthInfo();
    }

    /// <inheritdoc/>
    protected override async Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct)
    {
        if (_engine == null)
        {
            throw new InvalidOperationException("VdeStorageStrategy is not initialized.");
        }

        var health = await _engine.GetHealthReportAsync(ct);
        return health.FreeBlocks * _blockSize;
    }

    /// <summary>
    /// Ensures the container path is absolute. Relative paths resolve to the current working
    /// directory which is environment-dependent and leads to data in unexpected locations.
    /// </summary>
    private static string EnsureAbsoluteContainerPath(string path)
    {
        if (!System.IO.Path.IsPathRooted(path))
        {
            // Resolve relative to the process's base directory, not CWD, for predictability
            string baseDir = AppContext.BaseDirectory;
            path = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, path));
        }
        return path;
    }

    /// <summary>
    /// Gets a configuration value with a default if not found.
    /// </summary>
    private T? GetConfigValue<T>(string key, T? defaultValue = default)
    {
        if (_configuration.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    /// <summary>
    /// Disposes the VDE engine.
    /// </summary>
    protected override async ValueTask DisposeAsyncCore()
    {
        if (_engine != null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }

        await base.DisposeAsyncCore();
    }
}
