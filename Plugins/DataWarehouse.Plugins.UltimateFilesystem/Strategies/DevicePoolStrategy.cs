using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;
using StorageTier = DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice.StorageTier;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// Filesystem strategy wrapping <see cref="DevicePoolManager"/>, <see cref="HotSwapManager"/>,
/// <see cref="BaremetalBootstrap"/>, and <see cref="DeviceJournal"/> for device pool lifecycle,
/// hot-swap management, and bare-metal bootstrap operations.
/// Registered as a Block-category strategy for auto-discovery by <see cref="FilesystemStrategyRegistry"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool management strategy (BMDV-05/BMDV-06/BMDV-07/BMDV-10/BMDV-11/BMDV-12)")]
public sealed class DevicePoolStrategy : FilesystemStrategyBase
{
    private DevicePoolManager? _poolManager;
    private HotSwapManager? _hotSwapManager;
    private BaremetalBootstrap? _bootstrap;
    private DeviceJournal? _journal;

    /// <inheritdoc/>
    public override string StrategyId => "device-pool";

    /// <inheritdoc/>
    public override string DisplayName => "Device Pool Manager";

    /// <inheritdoc/>
    public override FilesystemStrategyCategory Category => FilesystemStrategyCategory.Block;

    /// <inheritdoc/>
    public override FilesystemStrategyCapabilities Capabilities => new()
    {
        SupportsDirectIo = false,
        SupportsAsyncIo = true,
        SupportsMmap = false,
        SupportsKernelBypass = false,
        SupportsVectoredIo = false,
        SupportsSparse = false,
        SupportsAutoDetect = false
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Device pool lifecycle management with hot-swap support and bare-metal bootstrap. " +
        "Creates named device pools with tier classification and locality tags, " +
        "manages hot-swap device addition/removal with journal-backed crash consistency, " +
        "and bootstraps DataWarehouse from raw physical devices.";

    /// <inheritdoc/>
    public override string[] Tags =>
        ["device-pool", "hot-swap", "bare-metal", "bootstrap", "journal", "storage-tier", "locality"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        var codec = new PoolMetadataCodec();
        _journal = new DeviceJournal();
        _poolManager = new DevicePoolManager(codec);

        // HotSwapManager and BaremetalBootstrap require a PhysicalDeviceManager.
        // These are wired lazily when the DeviceHealthStrategy provides the manager
        // via the plugin's message handler coordination. For standalone initialization,
        // create minimal instances.
        var discoveryService = new DeviceDiscoveryService();
        var smartMonitor = new SmartMonitor();
        var predictionEngine = new FailurePredictionEngine();
        var deviceManager = new PhysicalDeviceManager(
            discoveryService, smartMonitor, predictionEngine);

        _hotSwapManager = new HotSwapManager(deviceManager, _poolManager, _journal);
        _bootstrap = new BaremetalBootstrap(discoveryService, _poolManager, _journal, deviceManager);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        if (_hotSwapManager != null)
        {
            await _hotSwapManager.StopAsync().ConfigureAwait(false);
            await _hotSwapManager.DisposeAsync().ConfigureAwait(false);
        }

        if (_poolManager != null)
        {
            await _poolManager.DisposeAsync().ConfigureAwait(false);
        }

        if (_journal != null)
        {
            await _journal.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Use device.pool.* message topics instead.</exception>
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Use device.pool.* message topics");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Use device.pool.* message topics instead.</exception>
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Use device.pool.* message topics");

    /// <inheritdoc/>
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        var poolCount = 0;
        if (_poolManager != null)
        {
            var pools = await _poolManager.GetAllPoolsAsync().ConfigureAwait(false);
            poolCount = pools.Count;
        }

        return new FilesystemMetadata
        {
            FilesystemType = "device-pool",
            IsReadOnly = false
        };
    }

    // ========================================================================
    // Public methods called by plugin message handlers
    // ========================================================================

    /// <summary>
    /// Creates a new named device pool from a set of physical devices.
    /// </summary>
    /// <param name="name">Pool name.</param>
    /// <param name="tier">Optional explicit tier override.</param>
    /// <param name="locality">Optional locality tags.</param>
    /// <param name="devices">Physical devices to include.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created pool descriptor.</returns>
    public Task<DevicePoolDescriptor> CreatePoolAsync(
        string name,
        StorageTier? tier,
        LocalityTag? locality,
        IReadOnlyList<IPhysicalBlockDevice> devices,
        CancellationToken ct)
    {
        var manager = _poolManager ?? throw new InvalidOperationException("DevicePoolStrategy not initialized.");
        return manager.CreatePoolAsync(name, tier, locality, devices, ct);
    }

    /// <summary>
    /// Gets all registered device pools.
    /// </summary>
    /// <returns>A read-only list of all pool descriptors.</returns>
    public Task<IReadOnlyList<DevicePoolDescriptor>> GetPoolsAsync()
    {
        var manager = _poolManager ?? throw new InvalidOperationException("DevicePoolStrategy not initialized.");
        return manager.GetAllPoolsAsync();
    }

    /// <summary>
    /// Deletes a device pool by its unique ID.
    /// </summary>
    /// <param name="poolId">The pool ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task DeletePoolAsync(Guid poolId, CancellationToken ct)
    {
        var manager = _poolManager ?? throw new InvalidOperationException("DevicePoolStrategy not initialized.");
        return manager.DeletePoolAsync(poolId, ct);
    }

    /// <summary>
    /// Bootstraps DataWarehouse from raw physical devices.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Bootstrap result with restored pools and unpooled devices.</returns>
    public Task<BootstrapResult> BootstrapAsync(CancellationToken ct)
    {
        var boot = _bootstrap ?? throw new InvalidOperationException("DevicePoolStrategy not initialized.");
        return boot.BootstrapFromRawDevicesAsync(ct);
    }

    /// <summary>
    /// Starts the hot-swap manager for automatic device addition/removal handling.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task StartHotSwapAsync(CancellationToken ct)
    {
        var hotSwap = _hotSwapManager ?? throw new InvalidOperationException("DevicePoolStrategy not initialized.");
        return hotSwap.StartAsync(ct);
    }

    /// <summary>
    /// Stops the hot-swap manager.
    /// </summary>
    public Task StopHotSwapAsync()
    {
        var hotSwap = _hotSwapManager ?? throw new InvalidOperationException("DevicePoolStrategy not initialized.");
        return hotSwap.StopAsync();
    }

    /// <summary>
    /// Gets the underlying <see cref="HotSwapManager"/> for event subscription.
    /// </summary>
    /// <returns>The hot-swap manager instance, or null if not initialized.</returns>
    public HotSwapManager? GetHotSwapManager() => _hotSwapManager;

    /// <summary>
    /// Gets the underlying <see cref="DevicePoolManager"/> for direct pool operations.
    /// </summary>
    /// <returns>The pool manager instance, or null if not initialized.</returns>
    public DevicePoolManager? GetPoolManager() => _poolManager;
}
