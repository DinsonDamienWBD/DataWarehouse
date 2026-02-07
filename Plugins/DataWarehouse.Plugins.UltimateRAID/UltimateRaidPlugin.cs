using System.Collections.Concurrent;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateRAID;

/// <summary>
/// Ultimate RAID Plugin - Comprehensive RAID strategy solution consolidating all RAID implementations.
///
/// Implements 50+ RAID strategies across categories:
/// - Standard RAID (0, 1, 2, 3, 4, 5, 6, 1E, 5E, 6E)
/// - Nested RAID (10, 01, 50, 60, 100)
/// - Advanced RAID (DP, TP, ADG, RAID-Z, RAID-Z2, RAID-Z3)
/// - Vendor-Specific (Dell RAID 50E, HP RAID ADG, NetApp RAID-DP/TP)
/// - Software-Defined (Linux MD RAID, Windows Storage Spaces, ZFS RAID)
///
/// Features:
/// - Strategy pattern for RAID extensibility
/// - Auto-discovery of RAID strategies
/// - Unified API across all RAID levels
/// - Configurable stripe sizes
/// - Hot-spare support
/// - Online capacity expansion
/// - Hardware acceleration (Intel ISA-L)
/// - SMART health monitoring
/// - Rebuild progress tracking with ETA
/// - Scrubbing and verification
/// - Performance statistics
/// - Thread-safe operations
/// - XOR parity calculations
/// - Galois Field operations for erasure coding
/// </summary>
public sealed class UltimateRaidPlugin : PluginBase, IDisposable
{
    private readonly RaidStrategyRegistry _registry;
    private readonly ConcurrentDictionary<string, long> _usageStats = new();
    private readonly ConcurrentDictionary<string, RaidHealthStatus> _healthStatus = new();
    private bool _disposed;

    // Configuration
    private volatile string _defaultStrategyId = "raid1";
    private volatile bool _auditEnabled = true;
    private volatile bool _autoRebuildEnabled = true;
    private volatile int _maxConcurrentRebuilds = 2;

    // Statistics
    private long _totalWrites;
    private long _totalReads;
    private long _totalRebuilds;
    private long _totalScrubs;
    private long _totalVerifications;
    private long _totalFailures;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.raid.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate RAID";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.StorageProvider;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate RAID plugin providing 50+ RAID strategies including standard levels (0-6), nested RAID " +
        "(10, 50, 60), advanced RAID (DP, TP, ADG, RAID-Z), vendor-specific implementations, and software-defined " +
        "RAID. Supports hot-spare, online expansion, hardware acceleration, SMART monitoring, rebuild tracking, " +
        "scrubbing, and comprehensive statistics.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags => [
        "raid", "storage", "redundancy", "fault-tolerance", "performance",
        "parity", "mirroring", "striping", "erasure-coding", "rebuild"
    ];

    /// <summary>
    /// Gets the RAID strategy registry.
    /// </summary>
    public RaidStrategyRegistry Registry => _registry;

    /// <summary>
    /// Gets or sets whether audit logging is enabled.
    /// </summary>
    public bool AuditEnabled
    {
        get => _auditEnabled;
        set => _auditEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether automatic rebuild is enabled on hot-spare.
    /// </summary>
    public bool AutoRebuildEnabled
    {
        get => _autoRebuildEnabled;
        set => _autoRebuildEnabled = value;
    }

    /// <summary>
    /// Gets or sets the maximum number of concurrent rebuilds.
    /// </summary>
    public int MaxConcurrentRebuilds
    {
        get => _maxConcurrentRebuilds;
        set => _maxConcurrentRebuilds = value > 0 ? value : 1;
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate RAID plugin.
    /// </summary>
    public UltimateRaidPlugin()
    {
        _registry = new RaidStrategyRegistry();

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
        response.Metadata["AutoRebuildEnabled"] = _autoRebuildEnabled.ToString();
        response.Metadata["MaxConcurrentRebuilds"] = _maxConcurrentRebuilds.ToString();

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "raid.initialize", DisplayName = "Initialize RAID", Description = "Initialize a RAID array" },
            new() { Name = "raid.write", DisplayName = "Write", Description = "Write data to RAID array" },
            new() { Name = "raid.read", DisplayName = "Read", Description = "Read data from RAID array" },
            new() { Name = "raid.rebuild", DisplayName = "Rebuild", Description = "Rebuild failed disk" },
            new() { Name = "raid.verify", DisplayName = "Verify", Description = "Verify RAID integrity" },
            new() { Name = "raid.scrub", DisplayName = "Scrub", Description = "Scrub RAID array for errors" },
            new() { Name = "raid.health", DisplayName = "Health Check", Description = "Check RAID health status" },
            new() { Name = "raid.stats", DisplayName = "Statistics", Description = "Get RAID statistics" },
            new() { Name = "raid.add-disk", DisplayName = "Add Disk", Description = "Add disk to RAID array" },
            new() { Name = "raid.remove-disk", DisplayName = "Remove Disk", Description = "Remove disk from RAID array" },
            new() { Name = "raid.replace-disk", DisplayName = "Replace Disk", Description = "Replace failed disk" },
            new() { Name = "raid.list-strategies", DisplayName = "List Strategies", Description = "List available RAID strategies" },
            new() { Name = "raid.set-default", DisplayName = "Set Default", Description = "Set default RAID strategy" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.GetAllStrategies().Count;
        metadata["StandardStrategies"] = GetStrategiesByCategory("standard").Count;
        metadata["NestedStrategies"] = GetStrategiesByCategory("nested").Count;
        metadata["AdvancedStrategies"] = GetStrategiesByCategory("advanced").Count;
        metadata["VendorStrategies"] = GetStrategiesByCategory("vendor-specific").Count;
        metadata["TotalWrites"] = Interlocked.Read(ref _totalWrites);
        metadata["TotalReads"] = Interlocked.Read(ref _totalReads);
        metadata["TotalRebuilds"] = Interlocked.Read(ref _totalRebuilds);
        metadata["TotalScrubs"] = Interlocked.Read(ref _totalScrubs);
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "raid.initialize" => HandleInitializeAsync(message),
            "raid.write" => HandleWriteAsync(message),
            "raid.read" => HandleReadAsync(message),
            "raid.rebuild" => HandleRebuildAsync(message),
            "raid.verify" => HandleVerifyAsync(message),
            "raid.scrub" => HandleScrubAsync(message),
            "raid.health" => HandleHealthCheckAsync(message),
            "raid.stats" => HandleStatsAsync(message),
            "raid.add-disk" => HandleAddDiskAsync(message),
            "raid.remove-disk" => HandleRemoveDiskAsync(message),
            "raid.replace-disk" => HandleReplaceDiskAsync(message),
            "raid.list-strategies" => HandleListStrategiesAsync(message),
            "raid.set-default" => HandleSetDefaultAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    #region Message Handlers

    private async Task HandleInitializeAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        if (!message.Payload.TryGetValue("config", out var cfgObj) || cfgObj is not RaidConfiguration config)
        {
            throw new ArgumentException("Missing or invalid 'config' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        await strategy.InitializeAsync(config);

        message.Payload["success"] = true;
        message.Payload["arrayId"] = Guid.NewGuid().ToString();

        if (_auditEnabled)
        {
            // Log initialization
        }
    }

    private async Task HandleWriteAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        if (!message.Payload.TryGetValue("lba", out var lbaObj) || lbaObj is not long lba)
        {
            throw new ArgumentException("Missing 'lba' parameter");
        }

        if (!message.Payload.TryGetValue("data", out var dataObj) || dataObj is not byte[] data)
        {
            throw new ArgumentException("Missing or invalid 'data' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        await strategy.WriteAsync(lba, data);

        message.Payload["bytesWritten"] = data.Length;
        Interlocked.Increment(ref _totalWrites);
        IncrementUsageStats(strategyId);
    }

    private async Task HandleReadAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        if (!message.Payload.TryGetValue("lba", out var lbaObj) || lbaObj is not long lba)
        {
            throw new ArgumentException("Missing 'lba' parameter");
        }

        if (!message.Payload.TryGetValue("length", out var lenObj) || lenObj is not int length)
        {
            throw new ArgumentException("Missing 'length' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        var data = await strategy.ReadAsync(lba, length);

        message.Payload["data"] = data;
        message.Payload["bytesRead"] = data.Length;
        Interlocked.Increment(ref _totalReads);
    }

    private async Task HandleRebuildAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        if (!message.Payload.TryGetValue("diskIndex", out var idxObj) || idxObj is not int diskIndex)
        {
            throw new ArgumentException("Missing 'diskIndex' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        await strategy.RebuildAsync(diskIndex);

        message.Payload["rebuilt"] = true;
        Interlocked.Increment(ref _totalRebuilds);
    }

    private async Task HandleVerifyAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        var result = await strategy.VerifyAsync();

        message.Payload["result"] = result;
        Interlocked.Increment(ref _totalVerifications);
    }

    private async Task HandleScrubAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        var result = await strategy.ScrubAsync();

        message.Payload["result"] = result;
        Interlocked.Increment(ref _totalScrubs);
    }

    private async Task HandleHealthCheckAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        var health = await strategy.GetHealthStatusAsync();

        message.Payload["health"] = health;
        _healthStatus[strategyId] = health;
    }

    private async Task HandleStatsAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        var stats = await strategy.GetStatisticsAsync();

        message.Payload["statistics"] = stats;
    }

    private async Task HandleAddDiskAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        if (!message.Payload.TryGetValue("disk", out var diskObj) || diskObj is not VirtualDisk disk)
        {
            throw new ArgumentException("Missing or invalid 'disk' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        await strategy.AddDiskAsync(disk);

        message.Payload["added"] = true;
    }

    private async Task HandleRemoveDiskAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        if (!message.Payload.TryGetValue("diskIndex", out var idxObj) || idxObj is not int diskIndex)
        {
            throw new ArgumentException("Missing 'diskIndex' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        await strategy.RemoveDiskAsync(diskIndex);

        message.Payload["removed"] = true;
    }

    private async Task HandleReplaceDiskAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("strategyId", out var sidObj) || sidObj is not string strategyId)
        {
            throw new ArgumentException("Missing 'strategyId' parameter");
        }

        if (!message.Payload.TryGetValue("diskIndex", out var idxObj) || idxObj is not int diskIndex)
        {
            throw new ArgumentException("Missing 'diskIndex' parameter");
        }

        if (!message.Payload.TryGetValue("replacementDisk", out var diskObj) || diskObj is not VirtualDisk replacementDisk)
        {
            throw new ArgumentException("Missing or invalid 'replacementDisk' parameter");
        }

        var strategy = _registry.GetStrategy(strategyId)
            ?? throw new ArgumentException($"RAID strategy '{strategyId}' not found");

        await strategy.ReplaceDiskAsync(diskIndex, replacementDisk);

        message.Payload["replaced"] = true;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var strategies = _registry.GetAllStrategies();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["name"] = s.StrategyName,
            ["level"] = s.RaidLevel,
            ["category"] = s.Category,
            ["isAvailable"] = s.IsAvailable,
            ["minDisks"] = s.MinimumDisks,
            ["faultTolerance"] = s.FaultTolerance,
            ["storageEfficiency"] = s.StorageEfficiency,
            ["supportsHotSpare"] = s.SupportsHotSpare,
            ["supportsOnlineExpansion"] = s.SupportsOnlineExpansion,
            ["supportsHardwareAcceleration"] = s.SupportsHardwareAcceleration
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

    #endregion

    #region Helper Methods

    private List<IRaidStrategy> GetStrategiesByCategory(string category)
    {
        return _registry.GetStrategiesByCategory(category).ToList();
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

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Dispose all registered strategies
        foreach (var strategy in _registry.GetAllStrategies())
        {
            strategy.Dispose();
        }

        _usageStats.Clear();
        _healthStatus.Clear();
    }
}
