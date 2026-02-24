using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// Filesystem strategy wrapping <see cref="DeviceDiscoveryService"/> and <see cref="DeviceTopologyMapper"/>
/// for physical device enumeration and topology building. Registered as a Block-category strategy
/// so it is auto-discovered by <see cref="FilesystemStrategyRegistry"/>.
/// </summary>
/// <remarks>
/// This strategy does not perform block I/O itself. It exposes device discovery and topology
/// mapping through public methods that the plugin's device.discover / device.topology message
/// handlers delegate to. ReadBlockAsync / WriteBlockAsync throw <see cref="NotSupportedException"/>
/// because callers should use the message bus topics instead.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device discovery strategy (BMDV-01/BMDV-08)")]
public sealed class DeviceDiscoveryStrategy : FilesystemStrategyBase
{
    private DeviceDiscoveryService? _discoveryService;
    private DeviceTopologyMapper? _topologyMapper;

    /// <inheritdoc/>
    public override string StrategyId => "device-discovery";

    /// <inheritdoc/>
    public override string DisplayName => "Device Discovery";

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
        "Cross-platform physical device discovery and topology mapping. " +
        "Enumerates NVMe, SATA, SAS, USB, VirtIO, and network-attached storage devices " +
        "and builds a controller -> bus -> device topology tree with NUMA affinity.";

    /// <inheritdoc/>
    public override string[] Tags =>
        ["device-discovery", "topology", "nvme", "sata", "sas", "numa", "physical-device"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _discoveryService = new DeviceDiscoveryService();
        _topologyMapper = new DeviceTopologyMapper();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Use device.discover message topic instead.</exception>
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Use device.discover message topic");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Use device.discover message topic instead.</exception>
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Use device.discover message topic");

    /// <inheritdoc/>
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(new FilesystemMetadata
        {
            FilesystemType = "device-discovery",
            IsReadOnly = true
        });
    }

    // ========================================================================
    // Public methods called by plugin message handlers
    // ========================================================================

    /// <summary>
    /// Discovers all physical block devices on the current platform.
    /// </summary>
    /// <param name="options">Discovery filter options. Uses defaults if null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of discovered physical device information records.</returns>
    public Task<IReadOnlyList<PhysicalDeviceInfo>> DiscoverAsync(
        DeviceDiscoveryOptions? options, CancellationToken ct)
    {
        var service = _discoveryService ?? new DeviceDiscoveryService();
        return service.DiscoverDevicesAsync(options, ct);
    }

    /// <summary>
    /// Builds a controller -> bus -> device topology tree from discovered devices.
    /// </summary>
    /// <param name="devices">The list of discovered devices.</param>
    /// <returns>A complete device topology tree.</returns>
    public DeviceTopologyTree BuildTopology(IReadOnlyList<PhysicalDeviceInfo> devices)
    {
        var mapper = _topologyMapper ?? new DeviceTopologyMapper();
        return mapper.BuildTopology(devices);
    }

    /// <summary>
    /// Computes NUMA affinity information from a topology tree.
    /// </summary>
    /// <param name="tree">The topology tree to analyze.</param>
    /// <returns>NUMA affinity information per NUMA node.</returns>
    public IReadOnlyList<NumaAffinityInfo> GetNumaAffinity(DeviceTopologyTree tree)
    {
        var mapper = _topologyMapper ?? new DeviceTopologyMapper();
        return mapper.GetNumaAffinity(tree);
    }
}
