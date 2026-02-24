using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Linux hardware probe using sysfs and procfs.
    /// Discovers PCI, USB, NVMe, GPIO, I2C, SPI, and block devices.
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Linux sysfs/procfs-based hardware discovery (HAL-02)")]
    public sealed class LinuxHardwareProbe : IHardwareProbe
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private IReadOnlyList<HardwareDevice>? _lastDiscovery;
        private FileSystemWatcher? _devWatcher;
        private Timer? _debounceTimer;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="LinuxHardwareProbe"/> class.
        /// Starts FileSystemWatcher on /dev for hot-plug notifications.
        /// </summary>
        public LinuxHardwareProbe()
        {
            InitializeDevWatcher();
        }

        /// <inheritdoc />
        public event EventHandler<HardwareChangeEventArgs>? OnHardwareChanged;

        /// <inheritdoc />
        public async Task<IReadOnlyList<HardwareDevice>> DiscoverAsync(HardwareDeviceType? typeFilter = null, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var devices = new List<HardwareDevice>();

                // PCI devices
                if (ShouldDiscover(typeFilter, HardwareDeviceType.PciDevice))
                {
                    devices.AddRange(await DiscoverPciDevicesAsync(ct));
                }

                // USB devices
                if (ShouldDiscover(typeFilter, HardwareDeviceType.UsbDevice))
                {
                    devices.AddRange(await DiscoverUsbDevicesAsync(ct));
                }

                // NVMe controllers and namespaces
                if (ShouldDiscover(typeFilter, HardwareDeviceType.NvmeController | HardwareDeviceType.NvmeNamespace))
                {
                    devices.AddRange(await DiscoverNvmeDevicesAsync(ct));
                }

                // Block devices
                if (ShouldDiscover(typeFilter, HardwareDeviceType.BlockDevice))
                {
                    devices.AddRange(await DiscoverBlockDevicesAsync(ct));
                }

                // GPIO controllers
                if (ShouldDiscover(typeFilter, HardwareDeviceType.GpioController))
                {
                    devices.AddRange(await DiscoverGpioControllersAsync(ct));
                }

                // I2C buses
                if (ShouldDiscover(typeFilter, HardwareDeviceType.I2cBus))
                {
                    devices.AddRange(await DiscoverI2cBusesAsync(ct));
                }

                // SPI buses
                if (ShouldDiscover(typeFilter, HardwareDeviceType.SpiBus))
                {
                    devices.AddRange(await DiscoverSpiBusesAsync(ct));
                }

                // Apply filter if specified
                if (typeFilter.HasValue)
                {
                    devices = devices.Where(d => (d.Type & typeFilter.Value) != 0).ToList();
                }

                _lastDiscovery = devices.AsReadOnly();
                return _lastDiscovery;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<HardwareDevice?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
        {
            if (_lastDiscovery == null)
            {
                await DiscoverAsync(null, ct);
            }

            return _lastDiscovery?.FirstOrDefault(d => d.DeviceId == deviceId);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;

            _devWatcher?.Dispose();
            _debounceTimer?.Dispose();
            _lock.Dispose();

            _disposed = true;
        }

        private void InitializeDevWatcher()
        {
            try
            {
                if (!Directory.Exists("/dev"))
                    return;

                _devWatcher = new FileSystemWatcher("/dev")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true
                };

                _devWatcher.Created += OnDeviceFileChanged;
                _devWatcher.Deleted += OnDeviceFileChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.InitializeDevWatcher] {ex.GetType().Name}: {ex.Message}");
                _devWatcher = null;
            }
        }

        private void OnDeviceFileChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce rapid events (USB plug/unplug can cause floods)
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                // Fire change event after debounce period.
                // Device details from /dev path are limited; notify with a generic device entry.
                var changeType = e.ChangeType == WatcherChangeTypes.Created
                    ? HardwareChangeType.Added
                    : HardwareChangeType.Removed;
                OnHardwareChanged?.Invoke(this, new HardwareChangeEventArgs
                {
                    Device = new HardwareDevice
                    {
                        DeviceId = Path.GetFileName(e.FullPath),
                        Name = Path.GetFileName(e.FullPath),
                        Type = HardwareDeviceType.BlockDevice,
                        DevicePath = e.FullPath,
                        Properties = System.Collections.Immutable.ImmutableDictionary<string, string>.Empty
                    },
                    ChangeType = changeType
                });
            }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverPciDevicesAsync(CancellationToken ct)
        {
            var devices = new List<HardwareDevice>();
            const string pciBasePath = "/sys/bus/pci/devices";

            if (!Directory.Exists(pciBasePath))
                return devices;

            try
            {
                foreach (var devicePath in Directory.GetDirectories(pciBasePath))
                {
                    ct.ThrowIfCancellationRequested();

                    var deviceName = Path.GetFileName(devicePath);
                    var vendorId = await ReadSysfsFileAsync(Path.Combine(devicePath, "vendor"), ct);
                    var deviceId = await ReadSysfsFileAsync(Path.Combine(devicePath, "device"), ct);
                    var classCode = await ReadSysfsFileAsync(Path.Combine(devicePath, "class"), ct);

                    var deviceType = ClassifyPciDevice(classCode);
                    if (deviceType == HardwareDeviceType.None)
                        deviceType = HardwareDeviceType.PciDevice;

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = deviceName,
                        Name = $"PCI Device {deviceName}",
                        Type = deviceType,
                        VendorId = vendorId?.TrimStart('0', 'x'),
                        ProductId = deviceId?.TrimStart('0', 'x'),
                        Properties = ImmutableDictionary<string, string>.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.DiscoverPciDevicesAsync] {ex.GetType().Name}: {ex.Message}");
            }

            return devices;
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverUsbDevicesAsync(CancellationToken ct)
        {
            var devices = new List<HardwareDevice>();
            const string usbBasePath = "/sys/bus/usb/devices";

            if (!Directory.Exists(usbBasePath))
                return devices;

            try
            {
                foreach (var devicePath in Directory.GetDirectories(usbBasePath))
                {
                    ct.ThrowIfCancellationRequested();

                    // Skip USB hubs (they have bDeviceClass = 9)
                    var deviceClass = await ReadSysfsFileAsync(Path.Combine(devicePath, "bDeviceClass"), ct);
                    if (deviceClass == "09")
                        continue;

                    var deviceName = Path.GetFileName(devicePath);
                    var vendorId = await ReadSysfsFileAsync(Path.Combine(devicePath, "idVendor"), ct);
                    var productId = await ReadSysfsFileAsync(Path.Combine(devicePath, "idProduct"), ct);
                    var manufacturer = await ReadSysfsFileAsync(Path.Combine(devicePath, "manufacturer"), ct);
                    var product = await ReadSysfsFileAsync(Path.Combine(devicePath, "product"), ct);

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = deviceName,
                        Name = product ?? $"USB Device {deviceName}",
                        Type = HardwareDeviceType.UsbDevice,
                        Vendor = manufacturer,
                        VendorId = vendorId,
                        ProductId = productId,
                        Properties = ImmutableDictionary<string, string>.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.DiscoverUsbDevicesAsync] {ex.GetType().Name}: {ex.Message}");
            }

            return devices;
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverNvmeDevicesAsync(CancellationToken ct)
        {
            var devices = new List<HardwareDevice>();
            const string nvmeBasePath = "/sys/class/nvme";

            if (!Directory.Exists(nvmeBasePath))
                return devices;

            try
            {
                // NVMe controllers
                foreach (var controllerPath in Directory.GetDirectories(nvmeBasePath))
                {
                    ct.ThrowIfCancellationRequested();

                    var controllerName = Path.GetFileName(controllerPath);
                    var model = await ReadSysfsFileAsync(Path.Combine(controllerPath, "model"), ct);
                    var serial = await ReadSysfsFileAsync(Path.Combine(controllerPath, "serial"), ct);

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = controllerName,
                        Name = model ?? $"NVMe Controller {controllerName}",
                        Type = HardwareDeviceType.NvmeController,
                        DevicePath = $"/dev/{controllerName}",
                        Properties = ImmutableDictionary<string, string>.Empty
                    });

                    // NVMe namespaces
                    var namespacePattern = $"{controllerName}n*";
                    var namespaceBasePath = "/sys/class/nvme";
                    foreach (var namespacePath in Directory.GetDirectories(namespaceBasePath, namespacePattern))
                    {
                        ct.ThrowIfCancellationRequested();

                        var namespaceName = Path.GetFileName(namespacePath);
                        var size = await ReadSysfsFileAsync(Path.Combine(namespacePath, "size"), ct);

                        devices.Add(new HardwareDevice
                        {
                            DeviceId = namespaceName,
                            Name = $"NVMe Namespace {namespaceName}",
                            Type = HardwareDeviceType.NvmeNamespace,
                            DevicePath = $"/dev/{namespaceName}",
                            Properties = ImmutableDictionary<string, string>.Empty
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.DiscoverNvmeDevicesAsync] {ex.GetType().Name}: {ex.Message}");
            }

            return devices;
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverBlockDevicesAsync(CancellationToken ct)
        {
            var devices = new List<HardwareDevice>();
            const string blockBasePath = "/sys/class/block";

            if (!Directory.Exists(blockBasePath))
                return devices;

            try
            {
                foreach (var devicePath in Directory.GetDirectories(blockBasePath))
                {
                    ct.ThrowIfCancellationRequested();

                    // Skip partitions (they have a "partition" file)
                    if (File.Exists(Path.Combine(devicePath, "partition")))
                        continue;

                    var deviceName = Path.GetFileName(devicePath);
                    var model = await ReadSysfsFileAsync(Path.Combine(devicePath, "device", "model"), ct);
                    var vendor = await ReadSysfsFileAsync(Path.Combine(devicePath, "device", "vendor"), ct);

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = deviceName,
                        Name = model?.Trim() ?? $"Block Device {deviceName}",
                        Type = HardwareDeviceType.BlockDevice,
                        Vendor = vendor?.Trim(),
                        DevicePath = $"/dev/{deviceName}",
                        Properties = ImmutableDictionary<string, string>.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.DiscoverBlockDevicesAsync] {ex.GetType().Name}: {ex.Message}");
            }

            return devices;
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverGpioControllersAsync(CancellationToken ct)
        {
            var devices = new List<HardwareDevice>();
            const string gpioBasePath = "/sys/class/gpio";

            if (!Directory.Exists(gpioBasePath))
                return devices;

            try
            {
                // Check for gpiochip* entries
                foreach (var chipPath in Directory.GetDirectories(gpioBasePath, "gpiochip*"))
                {
                    ct.ThrowIfCancellationRequested();

                    var chipName = Path.GetFileName(chipPath);
                    var label = await ReadSysfsFileAsync(Path.Combine(chipPath, "label"), ct);

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = chipName,
                        Name = label ?? $"GPIO Controller {chipName}",
                        Type = HardwareDeviceType.GpioController,
                        Properties = ImmutableDictionary<string, string>.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.DiscoverGpioControllersAsync] {ex.GetType().Name}: {ex.Message}");
            }

            return devices;
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverI2cBusesAsync(CancellationToken ct)
        {
            var devices = new List<HardwareDevice>();
            const string i2cBasePath = "/sys/class/i2c-adapter";

            if (!Directory.Exists(i2cBasePath))
                return devices;

            try
            {
                foreach (var adapterPath in Directory.GetDirectories(i2cBasePath))
                {
                    ct.ThrowIfCancellationRequested();

                    var adapterName = Path.GetFileName(adapterPath);
                    var name = await ReadSysfsFileAsync(Path.Combine(adapterPath, "name"), ct);

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = adapterName,
                        Name = name ?? $"I2C Bus {adapterName}",
                        Type = HardwareDeviceType.I2cBus,
                        DevicePath = $"/dev/{adapterName}",
                        Properties = ImmutableDictionary<string, string>.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.DiscoverI2cBusesAsync] {ex.GetType().Name}: {ex.Message}");
            }

            return devices;
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverSpiBusesAsync(CancellationToken ct)
        {
            var devices = new List<HardwareDevice>();
            const string spiBasePath = "/sys/class/spi_master";

            if (!Directory.Exists(spiBasePath))
                return devices;

            try
            {
                foreach (var masterPath in Directory.GetDirectories(spiBasePath))
                {
                    ct.ThrowIfCancellationRequested();

                    var masterName = Path.GetFileName(masterPath);

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = masterName,
                        Name = $"SPI Bus {masterName}",
                        Type = HardwareDeviceType.SpiBus,
                        Properties = ImmutableDictionary<string, string>.Empty
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.DiscoverSpiBusesAsync] {ex.GetType().Name}: {ex.Message}");
            }

            return devices;
        }

        private static async Task<string?> ReadSysfsFileAsync(string path, CancellationToken ct)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                var content = await File.ReadAllTextAsync(path, ct);
                return content.Trim();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LinuxHardwareProbe.ReadSysfsFileAsync] {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static HardwareDeviceType ClassifyPciDevice(string? classCode)
        {
            if (string.IsNullOrEmpty(classCode))
                return HardwareDeviceType.None;

            // PCI class code format: 0xCCSS00 (CC = class, SS = subclass)
            // Class 0x03 = Display controller (GPU)
            // Class 0x01 = Mass storage controller
            // Class 0x02 = Network controller
            if (classCode.StartsWith("0x03", StringComparison.OrdinalIgnoreCase))
                return HardwareDeviceType.GpuAccelerator;

            if (classCode.StartsWith("0x01", StringComparison.OrdinalIgnoreCase))
                return HardwareDeviceType.BlockDevice;

            if (classCode.StartsWith("0x02", StringComparison.OrdinalIgnoreCase))
                return HardwareDeviceType.NetworkAdapter;

            return HardwareDeviceType.PciDevice;
        }

        private static bool ShouldDiscover(HardwareDeviceType? filter, HardwareDeviceType types)
        {
            if (!filter.HasValue) return true;
            return (filter.Value & types) != 0;
        }
    }
}
