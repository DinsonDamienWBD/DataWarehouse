using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Windows hardware probe using WMI (Windows Management Instrumentation).
    /// Discovers PCI, USB, NVMe, GPU, and block devices via System.Management.
    /// </summary>
    [SupportedOSPlatform("windows")]
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Windows WMI-based hardware discovery (HAL-02)")]
    public sealed class WindowsHardwareProbe : IHardwareProbe
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private IReadOnlyList<HardwareDevice>? _lastDiscovery;
        private ManagementEventWatcher? _eventWatcher;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="WindowsHardwareProbe"/> class.
        /// Starts WMI event watcher for hot-plug device notifications.
        /// </summary>
        public WindowsHardwareProbe()
        {
            InitializeEventWatcher();
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

                // Query PCI/USB devices via Win32_PnPEntity
                if (ShouldDiscover(typeFilter, HardwareDeviceType.PciDevice | HardwareDeviceType.UsbDevice))
                {
                    devices.AddRange(await DiscoverPnPDevicesAsync(ct));
                }

                // Query block devices via Win32_DiskDrive
                if (ShouldDiscover(typeFilter, HardwareDeviceType.BlockDevice | HardwareDeviceType.NvmeController))
                {
                    devices.AddRange(await DiscoverDiskDrivesAsync(ct));
                }

                // Query GPU devices via Win32_VideoController
                if (ShouldDiscover(typeFilter, HardwareDeviceType.GpuAccelerator))
                {
                    devices.AddRange(await DiscoverGpuDevicesAsync(ct));
                }

                // Query serial ports via Win32_SerialPort
                if (ShouldDiscover(typeFilter, HardwareDeviceType.SerialPort))
                {
                    devices.AddRange(await DiscoverSerialPortsAsync(ct));
                }

                // Apply filter if specified
                if (typeFilter.HasValue)
                {
                    devices = devices.Where(d => (d.Type & typeFilter.Value) != 0).ToList();
                }

                _lastDiscovery = devices.AsReadOnly();
                return _lastDiscovery;
            }
            catch (ManagementException)
            {
                // WMI access failed - return empty list (graceful degradation)
                return Array.Empty<HardwareDevice>();
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<HardwareDevice?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
        {
            // Ensure we have discovery results
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

            _eventWatcher?.Stop();
            _eventWatcher?.Dispose();
            _lock.Dispose();

            _disposed = true;
        }

        private void InitializeEventWatcher()
        {
            try
            {
                var query = new WqlEventQuery(
                    "SELECT * FROM __InstanceOperationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'");

                _eventWatcher = new ManagementEventWatcher(query);
                _eventWatcher.EventArrived += OnWmiEventArrived;
                _eventWatcher.Start();
            }
            catch (ManagementException)
            {
                // WMI event subscription failed - continue without events
                _eventWatcher = null;
            }
        }

        private void OnWmiEventArrived(object sender, EventArrivedEventArgs e)
        {
            try
            {
                var targetInstance = e.NewEvent["TargetInstance"] as ManagementBaseObject;
                if (targetInstance == null) return;

                var eventType = e.NewEvent.ClassPath.ClassName;
                var changeType = eventType switch
                {
                    "__InstanceCreationEvent" => HardwareChangeType.Added,
                    "__InstanceDeletionEvent" => HardwareChangeType.Removed,
                    "__InstanceModificationEvent" => HardwareChangeType.Modified,
                    _ => HardwareChangeType.Modified
                };

                var device = ParsePnPDevice(targetInstance);
                if (device != null)
                {
                    OnHardwareChanged?.Invoke(this, new HardwareChangeEventArgs
                    {
                        Device = device,
                        ChangeType = changeType
                    });
                }
            }
            catch
            {
                // Ignore event parsing errors
            }
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverPnPDevicesAsync(CancellationToken ct)
        {
            return await Task.Run<IEnumerable<HardwareDevice>>(() =>
            {
                try
                {
                    var devices = new List<HardwareDevice>();
                    using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity");
                    using var collection = searcher.Get();

                    foreach (ManagementObject obj in collection)
                    {
                        ct.ThrowIfCancellationRequested();
                        var device = ParsePnPDevice(obj);
                        if (device != null)
                        {
                            devices.Add(device);
                        }
                    }

                    return devices;
                }
                catch (ManagementException)
                {
                    return Array.Empty<HardwareDevice>();
                }
            }, ct);
        }

        private static HardwareDevice? ParsePnPDevice(ManagementBaseObject obj)
        {
            try
            {
                var deviceId = obj["DeviceID"]?.ToString();
                var name = obj["Name"]?.ToString();

                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(name))
                    return null;

                var description = obj["Description"]?.ToString();
                var manufacturer = obj["Manufacturer"]?.ToString();

                // Parse VendorId and ProductId from DeviceID
                string? vendorId = null;
                string? productId = null;
                var deviceType = HardwareDeviceType.None;

                if (deviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                {
                    deviceType = HardwareDeviceType.PciDevice;
                    ParsePciIds(deviceId, out vendorId, out productId);
                }
                else if (deviceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                {
                    deviceType = HardwareDeviceType.UsbDevice;
                    ParseUsbIds(deviceId, out vendorId, out productId);
                }

                return new HardwareDevice
                {
                    DeviceId = deviceId,
                    Name = name,
                    Description = description,
                    Type = deviceType,
                    Vendor = manufacturer,
                    VendorId = vendorId,
                    ProductId = productId,
                    Properties = ImmutableDictionary<string, string>.Empty
                };
            }
            catch
            {
                return null;
            }
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverDiskDrivesAsync(CancellationToken ct)
        {
            return await Task.Run<IEnumerable<HardwareDevice>>(() =>
            {
                try
                {
                    var devices = new List<HardwareDevice>();
                    using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                    using var collection = searcher.Get();

                    foreach (ManagementObject obj in collection)
                    {
                        ct.ThrowIfCancellationRequested();

                        var deviceId = obj["DeviceID"]?.ToString() ?? obj["PNPDeviceID"]?.ToString();
                        var name = obj["Model"]?.ToString() ?? "Unknown Disk";
                        var interfaceType = obj["InterfaceType"]?.ToString();

                        if (string.IsNullOrEmpty(deviceId))
                            continue;

                        var deviceType = interfaceType?.Contains("NVMe", StringComparison.OrdinalIgnoreCase) == true
                            ? HardwareDeviceType.NvmeController | HardwareDeviceType.BlockDevice
                            : HardwareDeviceType.BlockDevice;

                        devices.Add(new HardwareDevice
                        {
                            DeviceId = deviceId,
                            Name = name,
                            Type = deviceType,
                            DevicePath = obj["DeviceID"]?.ToString(),
                            Properties = ImmutableDictionary<string, string>.Empty
                        });
                    }

                    return devices;
                }
                catch (ManagementException)
                {
                    return Array.Empty<HardwareDevice>();
                }
            }, ct);
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverGpuDevicesAsync(CancellationToken ct)
        {
            return await Task.Run<IEnumerable<HardwareDevice>>(() =>
            {
                try
                {
                    var devices = new List<HardwareDevice>();
                    using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                    using var collection = searcher.Get();

                    foreach (ManagementObject obj in collection)
                    {
                        ct.ThrowIfCancellationRequested();

                        var deviceId = obj["PNPDeviceID"]?.ToString() ?? obj["DeviceID"]?.ToString();
                        var name = obj["Name"]?.ToString() ?? "Unknown GPU";

                        if (string.IsNullOrEmpty(deviceId))
                            continue;

                        devices.Add(new HardwareDevice
                        {
                            DeviceId = deviceId,
                            Name = name,
                            Type = HardwareDeviceType.GpuAccelerator,
                            DriverName = obj["DriverVersion"]?.ToString(),
                            Properties = ImmutableDictionary<string, string>.Empty
                        });
                    }

                    return devices;
                }
                catch (ManagementException)
                {
                    return Array.Empty<HardwareDevice>();
                }
            }, ct);
        }

        private static async Task<IEnumerable<HardwareDevice>> DiscoverSerialPortsAsync(CancellationToken ct)
        {
            return await Task.Run<IEnumerable<HardwareDevice>>(() =>
            {
                try
                {
                    var devices = new List<HardwareDevice>();
                    using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SerialPort");
                    using var collection = searcher.Get();

                    foreach (ManagementObject obj in collection)
                    {
                        ct.ThrowIfCancellationRequested();

                        var deviceId = obj["DeviceID"]?.ToString() ?? obj["PNPDeviceID"]?.ToString();
                        var name = obj["Name"]?.ToString() ?? "Unknown Serial Port";

                        if (string.IsNullOrEmpty(deviceId))
                            continue;

                        devices.Add(new HardwareDevice
                        {
                            DeviceId = deviceId,
                            Name = name,
                            Type = HardwareDeviceType.SerialPort,
                            DevicePath = obj["DeviceID"]?.ToString(),
                            Properties = ImmutableDictionary<string, string>.Empty
                        });
                    }

                    return devices;
                }
                catch (ManagementException)
                {
                    return Array.Empty<HardwareDevice>();
                }
            }, ct);
        }

        private static void ParsePciIds(string deviceId, out string? vendorId, out string? productId)
        {
            vendorId = null;
            productId = null;

            // Format: PCI\VEN_XXXX&DEV_XXXX&...
            var parts = deviceId.Split('\\', '&');
            foreach (var part in parts)
            {
                if (part.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase))
                {
                    vendorId = part.Substring(4);
                }
                else if (part.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase))
                {
                    productId = part.Substring(4);
                }
            }
        }

        private static void ParseUsbIds(string deviceId, out string? vendorId, out string? productId)
        {
            vendorId = null;
            productId = null;

            // Format: USB\VID_XXXX&PID_XXXX&...
            var parts = deviceId.Split('\\', '&');
            foreach (var part in parts)
            {
                if (part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                {
                    vendorId = part.Substring(4);
                }
                else if (part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                {
                    productId = part.Substring(4);
                }
            }
        }

        private static bool ShouldDiscover(HardwareDeviceType? filter, HardwareDeviceType types)
        {
            if (!filter.HasValue) return true;
            return (filter.Value & types) != 0;
        }
    }
}
