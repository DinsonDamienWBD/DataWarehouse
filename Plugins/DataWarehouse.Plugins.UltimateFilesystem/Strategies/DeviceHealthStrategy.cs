using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

namespace DataWarehouse.Plugins.UltimateFilesystem.Strategies;

/// <summary>
/// Filesystem strategy wrapping <see cref="PhysicalDeviceManager"/>, <see cref="SmartMonitor"/>,
/// and <see cref="FailurePredictionEngine"/> for device health monitoring and failure prediction.
/// Registered as a Block-category strategy for auto-discovery by <see cref="FilesystemStrategyRegistry"/>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device health monitoring strategy (BMDV-03/BMDV-04)")]
public sealed class DeviceHealthStrategy : FilesystemStrategyBase
{
    private SmartMonitor? _smartMonitor;
    private FailurePredictionEngine? _predictionEngine;
    private PhysicalDeviceManager? _deviceManager;

    /// <inheritdoc/>
    public override string StrategyId => "device-health";

    /// <inheritdoc/>
    public override string DisplayName => "Device Health Monitor";

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
        "SMART health monitoring and EWMA-based failure prediction for physical block devices. " +
        "Reads temperature, wear level, error counts, and power-on hours from NVMe and SATA devices " +
        "and produces risk-level assessments with estimated time-to-failure.";

    /// <inheritdoc/>
    public override string[] Tags =>
        ["smart", "health", "failure-prediction", "ewma", "temperature", "wear-level"];

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _smartMonitor = new SmartMonitor();
        _predictionEngine = new FailurePredictionEngine();

        var discoveryService = new DeviceDiscoveryService();
        _deviceManager = new PhysicalDeviceManager(
            discoveryService, _smartMonitor, _predictionEngine);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async Task DisposeCoreAsync()
    {
        if (_deviceManager != null)
        {
            await _deviceManager.StopAsync().ConfigureAwait(false);
            await _deviceManager.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<FilesystemMetadata?>(null);

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Use device.health message topic instead.</exception>
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Use device.health message topic");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Use device.health message topic instead.</exception>
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default) =>
        throw new NotSupportedException("Use device.health message topic");

    /// <inheritdoc/>
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default)
    {
        return Task.FromResult(new FilesystemMetadata
        {
            FilesystemType = "device-health",
            IsReadOnly = true
        });
    }

    // ========================================================================
    // Public methods called by plugin message handlers
    // ========================================================================

    /// <summary>
    /// Reads SMART health attributes from the specified device.
    /// </summary>
    /// <param name="devicePath">OS-specific device path (e.g., /dev/nvme0n1 or \\.\PhysicalDrive0).</param>
    /// <param name="busType">Bus type for selecting the appropriate SMART read strategy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health snapshot with available SMART data.</returns>
    public Task<PhysicalDeviceHealth> GetHealthAsync(string devicePath, BusType busType, CancellationToken ct)
    {
        var monitor = _smartMonitor ?? new SmartMonitor();
        return monitor.ReadSmartAttributesAsync(devicePath, busType, ct);
    }

    /// <summary>
    /// Produces a failure prediction for the specified device based on its latest health data.
    /// </summary>
    /// <param name="deviceId">Unique device identifier.</param>
    /// <param name="health">Current SMART health snapshot.</param>
    /// <returns>Failure prediction with risk level and estimated time-to-failure.</returns>
    public FailurePrediction GetPrediction(string deviceId, PhysicalDeviceHealth health)
    {
        var engine = _predictionEngine ?? new FailurePredictionEngine();
        return engine.UpdateAndPredict(deviceId, health);
    }

    /// <summary>
    /// Gets the underlying <see cref="PhysicalDeviceManager"/> for hot-swap integration.
    /// </summary>
    /// <returns>The device manager instance, or null if not initialized.</returns>
    public PhysicalDeviceManager? GetDeviceManager() => _deviceManager;
}
