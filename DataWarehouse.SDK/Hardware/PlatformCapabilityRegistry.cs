using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Configuration options for <see cref="PlatformCapabilityRegistry"/>.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Capability registry configuration (HAL-03)")]
    public sealed record PlatformCapabilityRegistryOptions
    {
        /// <summary>
        /// Gets the time-to-live for cached capability data.
        /// </summary>
        /// <remarks>
        /// After this duration expires, the next query will trigger an asynchronous refresh
        /// (but will still return cached results without blocking).
        /// Default is 5 minutes.
        /// </remarks>
        public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Gets the debounce duration for hardware change events.
        /// </summary>
        /// <remarks>
        /// When multiple hardware change events occur in rapid succession (e.g., USB device
        /// plug/unplug), the registry waits for this duration of silence before triggering
        /// a refresh. This prevents event flooding and excessive re-discovery operations.
        /// Default is 500ms.
        /// </remarks>
        public TimeSpan HardwareChangeDebounce { get; init; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Gets a value indicating whether to automatically refresh when hardware changes are detected.
        /// </summary>
        /// <remarks>
        /// When true, the registry subscribes to <see cref="IHardwareProbe.OnHardwareChanged"/>
        /// and automatically refreshes the capability cache (after debouncing).
        /// Default is true.
        /// </remarks>
        public bool AutoRefreshOnHardwareChange { get; init; } = true;
    }

    /// <summary>
    /// Cached implementation of <see cref="IPlatformCapabilityRegistry"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This implementation maintains an in-memory cache of discovered hardware devices and
    /// derived capability keys. The cache has a configurable TTL (default 5 minutes) and
    /// automatically refreshes when hardware change events are detected.
    /// </para>
    /// <para>
    /// <strong>Thread Safety:</strong> This class uses <see cref="ReaderWriterLockSlim"/> to
    /// allow concurrent reads while ensuring exclusive write access during cache updates.
    /// Refresh operations are serialized with <see cref="SemaphoreSlim"/> to prevent
    /// duplicate concurrent refresh operations.
    /// </para>
    /// <para>
    /// <strong>Capability Derivation:</strong> Capability keys are derived from discovered
    /// device types and properties. For example, an NVMe controller device produces "nvme"
    /// and "nvme.controller" capability keys. A GPU device with VendorId "10DE" (NVIDIA)
    /// produces "gpu" and "gpu.cuda" capability keys.
    /// </para>
    /// <para>
    /// <strong>Hardware Change Handling:</strong> The registry subscribes to hardware change
    /// events from the underlying probe. Events are debounced (default 500ms) to prevent
    /// flooding from rapid device plug/unplug cycles. After the debounce period, the registry
    /// triggers an asynchronous refresh.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Cached platform capability registry implementation (HAL-03)")]
    public sealed class PlatformCapabilityRegistry : IPlatformCapabilityRegistry
    {
        private readonly IHardwareProbe _probe;
        private readonly PlatformCapabilityRegistryOptions _options;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private readonly ReaderWriterLockSlim _cacheLock = new();
        private readonly object _debounceTimerLock = new();

        private IReadOnlyList<HardwareDevice> _devices = Array.Empty<HardwareDevice>();
        private HashSet<string> _capabilities = new();
        private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
        private Timer? _debounceTimer;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="PlatformCapabilityRegistry"/> class.
        /// </summary>
        /// <param name="probe">
        /// The hardware probe used to discover devices. This instance is not owned by the
        /// registry and will not be disposed.
        /// </param>
        /// <param name="options">
        /// Optional configuration. If null, default options are used.
        /// </param>
        public PlatformCapabilityRegistry(IHardwareProbe probe, PlatformCapabilityRegistryOptions? options = null)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
            _options = options ?? new PlatformCapabilityRegistryOptions();

            if (_options.AutoRefreshOnHardwareChange)
            {
                _probe.OnHardwareChanged += OnProbeHardwareChanged;
            }
        }

        /// <inheritdoc/>
        public event EventHandler<PlatformCapabilityChangedEventArgs>? OnCapabilityChanged;

        /// <summary>
        /// Initializes the capability registry by performing an initial hardware probe and capability derivation.
        /// </summary>
        /// <remarks>
        /// This method must be called before accessing any capability query methods (HasCapability, GetDevices, etc.).
        /// Failure to initialize will result in InvalidOperationException on first access.
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public bool HasCapability(string capabilityKey)
        {
            if (string.IsNullOrEmpty(capabilityKey))
            {
                throw new ArgumentException("Capability key cannot be null or empty.", nameof(capabilityKey));
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            // Cold start: if cache is uninitialized, throw - caller should call InitializeAsync() first
            if (_lastRefresh == DateTimeOffset.MinValue)
            {
                throw new InvalidOperationException(
                    "PlatformCapabilityRegistry has not been initialized. Call InitializeAsync() before accessing capabilities.");
            }

            // Check staleness and trigger background refresh if needed
            if (IsCacheStale())
            {
                _ = RefreshAsync();
            }

            _cacheLock.EnterReadLock();
            try
            {
                return _capabilities.Contains(capabilityKey);
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<HardwareDevice> GetDevices(HardwareDeviceType typeFilter)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Cold start check: throw if not initialized
            if (_lastRefresh == DateTimeOffset.MinValue)
            {
                throw new InvalidOperationException(
                    "PlatformCapabilityRegistry has not been initialized. Call InitializeAsync() before accessing capabilities.");
            }

            // Check staleness and trigger background refresh if needed
            if (IsCacheStale())
            {
                _ = RefreshAsync();
            }

            _cacheLock.EnterReadLock();
            try
            {
                return _devices.Where(d => d.Type.HasFlag(typeFilter)).ToList().AsReadOnly();
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<HardwareDevice> GetAllDevices()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Cold start check: throw if not initialized
            if (_lastRefresh == DateTimeOffset.MinValue)
            {
                throw new InvalidOperationException(
                    "PlatformCapabilityRegistry has not been initialized. Call InitializeAsync() before accessing capabilities.");
            }

            // Check staleness and trigger background refresh if needed
            if (IsCacheStale())
            {
                _ = RefreshAsync();
            }

            _cacheLock.EnterReadLock();
            try
            {
                return _devices;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <inheritdoc/>
        public IReadOnlyList<string> GetCapabilities()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Cold start check: throw if not initialized
            if (_lastRefresh == DateTimeOffset.MinValue)
            {
                throw new InvalidOperationException(
                    "PlatformCapabilityRegistry has not been initialized. Call InitializeAsync() before accessing capabilities.");
            }

            // Check staleness and trigger background refresh if needed
            if (IsCacheStale())
            {
                _ = RefreshAsync();
            }

            _cacheLock.EnterReadLock();
            try
            {
                return _capabilities.ToList().AsReadOnly();
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <inheritdoc/>
        public int GetDeviceCount(HardwareDeviceType typeFilter)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Cold start check: throw if not initialized
            if (_lastRefresh == DateTimeOffset.MinValue)
            {
                throw new InvalidOperationException(
                    "PlatformCapabilityRegistry has not been initialized. Call InitializeAsync() before accessing capabilities.");
            }

            // Check staleness and trigger background refresh if needed
            if (IsCacheStale())
            {
                _ = RefreshAsync();
            }

            _cacheLock.EnterReadLock();
            try
            {
                return _devices.Count(d => d.Type.HasFlag(typeFilter));
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }
        }

        /// <inheritdoc/>
        public async Task RefreshAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Try to acquire refresh lock (non-blocking)
            // If already refreshing, skip to avoid duplicate work
            if (!await _refreshLock.WaitAsync(0, ct))
            {
                return;
            }

            try
            {
                // Discover all hardware devices
                var newDevices = await _probe.DiscoverAsync(typeFilter: null, ct: ct);

                // Derive capabilities from devices
                var newCapabilities = DeriveCapabilities(newDevices);

                // Compute delta for event notification
                List<string> addedCapabilities;
                List<string> removedCapabilities;
                List<HardwareDevice> addedDevices;
                List<HardwareDevice> removedDevices;

                _cacheLock.EnterReadLock();
                try
                {
                    var oldCapabilitySet = _capabilities;
                    var oldDeviceSet = new HashSet<string>(_devices.Select(d => d.DeviceId));

                    addedCapabilities = newCapabilities.Except(oldCapabilitySet).ToList();
                    removedCapabilities = oldCapabilitySet.Except(newCapabilities).ToList();

                    addedDevices = newDevices.Where(d => !oldDeviceSet.Contains(d.DeviceId)).ToList();
                    removedDevices = _devices.Where(d => !newDevices.Any(nd => nd.DeviceId == d.DeviceId)).ToList();
                }
                finally
                {
                    _cacheLock.ExitReadLock();
                }

                // Update cache with exclusive write lock
                _cacheLock.EnterWriteLock();
                try
                {
                    _devices = newDevices;
                    _capabilities = newCapabilities;
                    _lastRefresh = DateTimeOffset.UtcNow;
                }
                finally
                {
                    _cacheLock.ExitWriteLock();
                }

                // Fire change event if delta is non-empty
                bool hasChanges = addedCapabilities.Count > 0 || removedCapabilities.Count > 0 ||
                                  addedDevices.Count > 0 || removedDevices.Count > 0;

                if (hasChanges && OnCapabilityChanged != null)
                {
                    var eventArgs = new PlatformCapabilityChangedEventArgs
                    {
                        AddedCapabilities = addedCapabilities.AsReadOnly(),
                        RemovedCapabilities = removedCapabilities.AsReadOnly(),
                        AddedDevices = addedDevices.AsReadOnly(),
                        RemovedDevices = removedDevices.AsReadOnly()
                    };

                    OnCapabilityChanged.Invoke(this, eventArgs);
                }
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> HasCapabilityAsync(string capabilityKey, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(capabilityKey))
                throw new ArgumentException("Capability key cannot be null or empty.", nameof(capabilityKey));

            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_lastRefresh == DateTimeOffset.MinValue)
                await RefreshAsync(ct);

            if (IsCacheStale())
                _ = RefreshAsync(ct);

            _cacheLock.EnterReadLock();
            try { return _capabilities.Contains(capabilityKey); }
            finally { _cacheLock.ExitReadLock(); }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<HardwareDevice>> GetDevicesAsync(HardwareDeviceType typeFilter, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_lastRefresh == DateTimeOffset.MinValue)
                await RefreshAsync(ct);

            if (IsCacheStale())
                _ = RefreshAsync(ct);

            _cacheLock.EnterReadLock();
            try { return _devices.Where(d => d.Type.HasFlag(typeFilter)).ToList().AsReadOnly(); }
            finally { _cacheLock.ExitReadLock(); }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<HardwareDevice>> GetAllDevicesAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_lastRefresh == DateTimeOffset.MinValue)
                await RefreshAsync(ct);

            if (IsCacheStale())
                _ = RefreshAsync(ct);

            _cacheLock.EnterReadLock();
            try { return _devices; }
            finally { _cacheLock.ExitReadLock(); }
        }

        /// <inheritdoc/>
        public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_lastRefresh == DateTimeOffset.MinValue)
                await RefreshAsync(ct);

            if (IsCacheStale())
                _ = RefreshAsync(ct);

            _cacheLock.EnterReadLock();
            try { return _capabilities.ToList().AsReadOnly(); }
            finally { _cacheLock.ExitReadLock(); }
        }

        /// <inheritdoc/>
        public async Task<int> GetDeviceCountAsync(HardwareDeviceType typeFilter, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_lastRefresh == DateTimeOffset.MinValue)
                await RefreshAsync(ct);

            if (IsCacheStale())
                _ = RefreshAsync(ct);

            _cacheLock.EnterReadLock();
            try { return _devices.Count(d => d.Type.HasFlag(typeFilter)); }
            finally { _cacheLock.ExitReadLock(); }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Unsubscribe from hardware change events
            if (_options.AutoRefreshOnHardwareChange)
            {
                _probe.OnHardwareChanged -= OnProbeHardwareChanged;
            }

            // Dispose debounce timer
            lock (_debounceTimerLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }

            // Dispose synchronization primitives
            _refreshLock.Dispose();
            _cacheLock.Dispose();
        }

        /// <summary>
        /// Checks if the cache is stale (older than TTL).
        /// </summary>
        private bool IsCacheStale()
        {
            return _lastRefresh + _options.CacheTtl < DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Handles hardware change events from the probe.
        /// </summary>
        private void OnProbeHardwareChanged(object? sender, HardwareChangeEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            // Reset debounce timer
            lock (_debounceTimerLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(
                    callback: _ => _ = RefreshAsync(CancellationToken.None),
                    state: null,
                    dueTime: _options.HardwareChangeDebounce,
                    period: Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Derives capability keys from a list of hardware devices.
        /// </summary>
        private static HashSet<string> DeriveCapabilities(IReadOnlyList<HardwareDevice> devices)
        {
            var capabilities = new HashSet<string>(StringComparer.Ordinal);

            foreach (var device in devices)
            {
                // NVMe devices
                if (device.Type.HasFlag(HardwareDeviceType.NvmeController))
                {
                    capabilities.Add("nvme");
                    capabilities.Add("nvme.controller");
                }

                if (device.Type.HasFlag(HardwareDeviceType.NvmeNamespace))
                {
                    capabilities.Add("nvme");
                    capabilities.Add("nvme.namespace");
                }

                // GPU accelerators
                if (device.Type.HasFlag(HardwareDeviceType.GpuAccelerator))
                {
                    capabilities.Add("gpu");

                    // Derive vendor-specific GPU capabilities
                    if (device.VendorId != null || device.Vendor != null)
                    {
                        // NVIDIA: VendorId 0x10DE
                        if (device.VendorId?.Contains("10DE", StringComparison.OrdinalIgnoreCase) == true ||
                            device.Vendor?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            capabilities.Add("gpu.cuda");
                        }

                        // AMD: VendorId 0x1002
                        if (device.VendorId?.Contains("1002", StringComparison.OrdinalIgnoreCase) == true ||
                            device.Vendor?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true ||
                            device.Vendor?.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            capabilities.Add("gpu.rocm");
                        }

                        // Intel: VendorId 0x8086
                        if (device.VendorId?.Contains("8086", StringComparison.OrdinalIgnoreCase) == true ||
                            device.Vendor?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            capabilities.Add("gpu.directml");
                        }
                    }
                }

                // QAT accelerators
                if (device.Type.HasFlag(HardwareDeviceType.QatAccelerator))
                {
                    capabilities.Add("qat");
                    capabilities.Add("qat.compression");
                    capabilities.Add("qat.encryption");
                }

                // TPM devices
                if (device.Type.HasFlag(HardwareDeviceType.TpmDevice))
                {
                    capabilities.Add("tpm2");
                    capabilities.Add("tpm2.sealing");
                    capabilities.Add("tpm2.attestation");
                }

                // HSM devices
                if (device.Type.HasFlag(HardwareDeviceType.HsmDevice))
                {
                    capabilities.Add("hsm");
                    capabilities.Add("hsm.pkcs11");
                }

                // GPIO controllers (edge/IoT)
                if (device.Type.HasFlag(HardwareDeviceType.GpioController))
                {
                    capabilities.Add("gpio");
                }

                // I2C buses
                if (device.Type.HasFlag(HardwareDeviceType.I2cBus))
                {
                    capabilities.Add("i2c");
                }

                // SPI buses
                if (device.Type.HasFlag(HardwareDeviceType.SpiBus))
                {
                    capabilities.Add("spi");
                }

                // Block devices
                if (device.Type.HasFlag(HardwareDeviceType.BlockDevice))
                {
                    capabilities.Add("block");
                }

                // Network adapters
                if (device.Type.HasFlag(HardwareDeviceType.NetworkAdapter))
                {
                    capabilities.Add("network");
                }

                // USB devices
                if (device.Type.HasFlag(HardwareDeviceType.UsbDevice))
                {
                    capabilities.Add("usb");
                }
            }

            return capabilities;
        }
    }
}
