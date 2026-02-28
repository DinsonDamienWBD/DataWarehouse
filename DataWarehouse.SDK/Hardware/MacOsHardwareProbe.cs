using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// macOS hardware probe using system_profiler.
    /// Provides basic hardware discovery via system_profiler JSON output.
    /// Falls back to NullHardwareProbe behavior if system_profiler is unavailable.
    /// </summary>
    /// <remarks>
    /// This is a minimal implementation for macOS. Full IOKit P/Invoke support
    /// is deferred to Phase 35 if needed. Hardware change events are not supported.
    /// </remarks>
    [SupportedOSPlatform("macos")]
    [SdkCompatibility("3.0.0", Notes = "Phase 32: macOS system_profiler-based hardware discovery (HAL-02)")]
    public sealed class MacOsHardwareProbe : IHardwareProbe
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private IReadOnlyList<HardwareDevice>? _lastDiscovery;
        private bool _disposed;

        /// <inheritdoc />
        /// <remarks>
        /// Hardware change events are not supported on macOS in this implementation.
        /// The event is declared but never fired.
        /// </remarks>
        public event EventHandler<HardwareChangeEventArgs>? OnHardwareChanged
        {
            add { }
            remove { }
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<HardwareDevice>> DiscoverAsync(HardwareDeviceType? typeFilter = null, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct);
            try
            {
                var devices = new List<HardwareDevice>();

                // Use system_profiler to get hardware information as JSON
                var json = await RunSystemProfilerAsync(ct);
                if (json != null)
                {
                    devices.AddRange(ParseSystemProfilerOutput(json));
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

            _lock.Dispose();
            _disposed = true;
        }

        private static async Task<string?> RunSystemProfilerAsync(CancellationToken ct)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "system_profiler",
                    Arguments = "SPHardwareDataType SPStorageDataType SPDisplaysDataType SPUSBDataType -json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return null;

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                return process.ExitCode == 0 ? output : null;
            }
            catch
            {
                // system_profiler not available or permission denied
                return null;
            }
        }

        private static IEnumerable<HardwareDevice> ParseSystemProfilerOutput(string json)
        {
            var devices = new List<HardwareDevice>();

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Parse storage devices
                if (root.TryGetProperty("SPStorageDataType", out var storage))
                {
                    devices.AddRange(ParseStorageDevices(storage));
                }

                // Parse display/GPU devices
                if (root.TryGetProperty("SPDisplaysDataType", out var displays))
                {
                    devices.AddRange(ParseDisplayDevices(displays));
                }

                // Parse USB devices
                if (root.TryGetProperty("SPUSBDataType", out var usb))
                {
                    devices.AddRange(ParseUsbDevices(usb));
                }
            }
            catch
            {
                // JSON parsing error - return partial results
            }

            return devices;
        }

        private static IEnumerable<HardwareDevice> ParseStorageDevices(JsonElement storageArray)
        {
            var devices = new List<HardwareDevice>();

            if (storageArray.ValueKind != JsonValueKind.Array)
                return devices;

            foreach (var item in storageArray.EnumerateArray())
            {
                try
                {
                    var name = item.TryGetProperty("_name", out var nameEl) ? nameEl.GetString() : "Unknown Storage";
                    var rawBsdName = item.TryGetProperty("bsd_name", out var bsdEl) ? bsdEl.GetString() : null;
                    var mediaType = item.TryGetProperty("medium_type", out var typeEl) ? typeEl.GetString() : null;

                    // Sanitize bsd_name: macOS BSD names are simple identifiers like "disk0", "disk1s1".
                    // Reject any value containing path separators or dots-only segments to prevent
                    // path traversal via a crafted system_profiler output (finding P2-396).
                    string? deviceId = null;
                    if (rawBsdName != null && IsSafeBsdName(rawBsdName))
                        deviceId = rawBsdName;

                    var deviceType = mediaType?.Contains("SSD", StringComparison.OrdinalIgnoreCase) == true
                        ? HardwareDeviceType.BlockDevice | HardwareDeviceType.NvmeController
                        : HardwareDeviceType.BlockDevice;

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = deviceId ?? name ?? "unknown",
                        Name = name ?? "Unknown Storage",
                        Type = deviceType,
                        DevicePath = deviceId != null ? $"/dev/{deviceId}" : null,
                        Properties = ImmutableDictionary<string, string>.Empty
                    });
                }
                catch
                {
                    // Skip malformed entries
                }
            }

            return devices;
        }

        private static IEnumerable<HardwareDevice> ParseDisplayDevices(JsonElement displaysArray)
        {
            var devices = new List<HardwareDevice>();

            if (displaysArray.ValueKind != JsonValueKind.Array)
                return devices;

            foreach (var item in displaysArray.EnumerateArray())
            {
                try
                {
                    var name = item.TryGetProperty("_name", out var nameEl) ? nameEl.GetString() : "Unknown GPU";

                    // Try to find chipset model
                    if (item.TryGetProperty("sppci_model", out var modelEl))
                    {
                        name = modelEl.GetString() ?? name;
                    }

                    var deviceId = $"gpu_{name?.Replace(" ", "_") ?? "unknown"}";

                    devices.Add(new HardwareDevice
                    {
                        DeviceId = deviceId,
                        Name = name ?? "Unknown GPU",
                        Type = HardwareDeviceType.GpuAccelerator,
                        Properties = ImmutableDictionary<string, string>.Empty
                    });
                }
                catch
                {
                    // Skip malformed entries
                }
            }

            return devices;
        }

        private static IEnumerable<HardwareDevice> ParseUsbDevices(JsonElement usbArray)
        {
            var devices = new List<HardwareDevice>();

            if (usbArray.ValueKind != JsonValueKind.Array)
                return devices;

            foreach (var item in usbArray.EnumerateArray())
            {
                try
                {
                    // Recursively parse USB tree
                    devices.AddRange(ParseUsbDeviceRecursive(item));
                }
                catch
                {
                    // Skip malformed entries
                }
            }

            return devices;
        }

        private static IEnumerable<HardwareDevice> ParseUsbDeviceRecursive(JsonElement usbItem)
        {
            var devices = new List<HardwareDevice>();

            try
            {
                var name = usbItem.TryGetProperty("_name", out var nameEl) ? nameEl.GetString() : "Unknown USB Device";
                var vendorId = usbItem.TryGetProperty("vendor_id", out var vidEl) ? vidEl.GetString() : null;
                var productId = usbItem.TryGetProperty("product_id", out var pidEl) ? pidEl.GetString() : null;
                var manufacturer = usbItem.TryGetProperty("manufacturer", out var mfgEl) ? mfgEl.GetString() : null;

                // Skip USB hubs and root hubs
                if (name?.Contains("Hub", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // But still process child devices
                    if (usbItem.TryGetProperty("_items", out var items))
                    {
                        foreach (var child in items.EnumerateArray())
                        {
                            devices.AddRange(ParseUsbDeviceRecursive(child));
                        }
                    }
                    return devices;
                }

                var deviceId = $"usb_{vendorId ?? "0000"}_{productId ?? "0000"}";

                devices.Add(new HardwareDevice
                {
                    DeviceId = deviceId,
                    Name = name ?? "Unknown USB Device",
                    Type = HardwareDeviceType.UsbDevice,
                    Vendor = manufacturer,
                    VendorId = vendorId,
                    ProductId = productId,
                    Properties = ImmutableDictionary<string, string>.Empty
                });

                // Recursively process child devices
                if (usbItem.TryGetProperty("_items", out var childItems))
                {
                    foreach (var child in childItems.EnumerateArray())
                    {
                        devices.AddRange(ParseUsbDeviceRecursive(child));
                    }
                }
            }
            catch
            {
                // Skip malformed entries
            }

            return devices;
        }

        /// <summary>
        /// Returns true if the BSD device name is safe to use in a /dev/ path.
        /// Valid macOS BSD names consist only of ASCII alphanumerics plus 's' and digit
        /// suffixes (e.g., "disk0", "disk1s1"). Rejects any name containing '/', '\', '..',
        /// or non-ASCII characters (finding P2-396).
        /// </summary>
        private static bool IsSafeBsdName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 32)
                return false;

            foreach (var ch in name)
            {
                // Allow only ASCII letters, digits, and underscore.
                // Reject '/', '\', '.', spaces, or any non-ASCII char.
                if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
                    return false;
            }

            return true;
        }
    }
}
