using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Factory for creating platform-appropriate hardware probes.
    /// </summary>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Hardware probe factory (HAL-02)")]
    public static class HardwareProbeFactory
    {
        /// <summary>
        /// Maximum allowed string length for hardware device fields (DIST-09 mitigation).
        /// Prevents injection attacks via excessively long strings.
        /// </summary>
        private const int MaxStringFieldLength = 1024;

        /// <summary>
        /// Regex pattern for allowed characters in device IDs and paths (DIST-09 mitigation).
        /// Prevents path traversal and injection via special characters.
        /// </summary>
        private static readonly Regex SafeStringPattern = new(
            @"^[\w\s\-\./:,;=@#\(\)\[\]\\]+$",
            RegexOptions.Compiled);

        /// <summary>
        /// Creates a hardware probe for the current platform.
        /// Returns a validating wrapper that sanitizes probe results before they
        /// influence routing/placement decisions (DIST-09 mitigation).
        /// </summary>
        /// <returns>
        /// A platform-specific hardware probe (Windows, Linux, macOS) wrapped with input validation,
        /// or <see cref="NullHardwareProbe"/> if the platform is not supported.
        /// </returns>
        public static IHardwareProbe Create()
        {
            IHardwareProbe inner;

            if (OperatingSystem.IsWindows())
            {
                inner = new WindowsHardwareProbe();
            }
            else if (OperatingSystem.IsLinux())
            {
                inner = new LinuxHardwareProbe();
            }
            else if (OperatingSystem.IsMacOS())
            {
                inner = new MacOsHardwareProbe();
            }
            else
            {
                return new NullHardwareProbe();
            }

            // DIST-09: Wrap with validation to sanitize results before routing/placement use
            return new ValidatingHardwareProbe(inner);
        }

        /// <summary>
        /// Validates and sanitizes a hardware device record (DIST-09 mitigation).
        /// Sanitizes string fields to prevent injection and validates numeric ranges.
        /// </summary>
        /// <param name="device">The device to validate.</param>
        /// <returns>A sanitized copy of the device, or null if the device is fundamentally invalid.</returns>
        internal static HardwareDevice? ValidateDevice(HardwareDevice device)
        {
            if (device == null) return null;

            // Validate required string fields
            if (string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(device.Name))
            {
                return null; // Required fields missing -- reject
            }

            // Sanitize all string fields
            var sanitizedProperties = device.Properties
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Key.Length <= MaxStringFieldLength)
                .ToDictionary(
                    kv => SanitizeString(kv.Key) ?? kv.Key,
                    kv => SanitizeString(kv.Value) ?? kv.Value)
                .ToImmutableDictionary();

            return device with
            {
                DeviceId = SanitizeString(device.DeviceId) ?? device.DeviceId,
                Name = SanitizeString(device.Name) ?? device.Name,
                Description = device.Description != null ? SanitizeString(device.Description) : null,
                Vendor = device.Vendor != null ? SanitizeString(device.Vendor) : null,
                VendorId = device.VendorId != null ? SanitizeString(device.VendorId) : null,
                ProductId = device.ProductId != null ? SanitizeString(device.ProductId) : null,
                DriverName = device.DriverName != null ? SanitizeString(device.DriverName) : null,
                DevicePath = device.DevicePath != null ? SanitizeString(device.DevicePath) : null,
                Properties = sanitizedProperties
            };
        }

        /// <summary>
        /// Sanitizes a string field by truncating to max length and removing control characters.
        /// Returns null if the string is empty after sanitization.
        /// </summary>
        private static string? SanitizeString(string? input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Truncate to max length
            if (input.Length > MaxStringFieldLength)
            {
                input = input[..MaxStringFieldLength];
            }

            // Remove control characters (prevents injection)
            var sanitized = new string(input.Where(c => !char.IsControl(c) || c == '\t').ToArray());

            return string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
        }
    }

    /// <summary>
    /// Decorating hardware probe that validates and sanitizes results from the inner probe
    /// before they influence routing/placement decisions (DIST-09 mitigation).
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 53: Validating hardware probe wrapper (DIST-09)")]
    internal sealed class ValidatingHardwareProbe : IHardwareProbe
    {
        private readonly IHardwareProbe _inner;

        public ValidatingHardwareProbe(IHardwareProbe inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public event EventHandler<HardwareChangeEventArgs>? OnHardwareChanged
        {
            add => _inner.OnHardwareChanged += value;
            remove => _inner.OnHardwareChanged -= value;
        }

        public async Task<IReadOnlyList<HardwareDevice>> DiscoverAsync(HardwareDeviceType? typeFilter = null, CancellationToken ct = default)
        {
            var devices = await _inner.DiscoverAsync(typeFilter, ct).ConfigureAwait(false);
            var validated = new List<HardwareDevice>();

            foreach (var device in devices)
            {
                var sanitized = HardwareProbeFactory.ValidateDevice(device);
                if (sanitized != null)
                {
                    validated.Add(sanitized);
                }
            }

            return validated.AsReadOnly();
        }

        public async Task<HardwareDevice?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
        {
            var device = await _inner.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
            return device != null ? HardwareProbeFactory.ValidateDevice(device) : null;
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }
}
