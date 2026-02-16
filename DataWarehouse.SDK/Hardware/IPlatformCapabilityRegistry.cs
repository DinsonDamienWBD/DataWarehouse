using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Registry that provides semantic queries about platform hardware capabilities.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The capability registry provides a semantic query API over discovered hardware,
    /// answering questions like "Does this machine have QAT compression?" or "How many
    /// NVMe controllers are present?". Instead of requiring callers to interpret raw
    /// device records, the registry provides boolean queries via hierarchical capability keys.
    /// </para>
    /// <para>
    /// <strong>Capability Keys:</strong> Capability keys are dot-separated hierarchical strings
    /// that describe hardware features. Examples include:
    /// <list type="bullet">
    ///   <item><description><c>nvme</c> - NVMe storage is present</description></item>
    ///   <item><description><c>nvme.controller</c> - NVMe controller device</description></item>
    ///   <item><description><c>nvme.namespace</c> - NVMe namespace device</description></item>
    ///   <item><description><c>gpu</c> - GPU accelerator is present</description></item>
    ///   <item><description><c>gpu.cuda</c> - NVIDIA CUDA-capable GPU</description></item>
    ///   <item><description><c>gpu.rocm</c> - AMD ROCm-capable GPU</description></item>
    ///   <item><description><c>gpu.directml</c> - Intel DirectML-capable GPU</description></item>
    ///   <item><description><c>qat</c> - Intel QuickAssist Technology is present</description></item>
    ///   <item><description><c>qat.compression</c> - QAT compression offload</description></item>
    ///   <item><description><c>qat.encryption</c> - QAT encryption offload</description></item>
    ///   <item><description><c>tpm2</c> - Trusted Platform Module 2.0</description></item>
    ///   <item><description><c>tpm2.sealing</c> - TPM sealing capability</description></item>
    ///   <item><description><c>tpm2.attestation</c> - TPM attestation capability</description></item>
    ///   <item><description><c>hsm</c> - Hardware Security Module</description></item>
    ///   <item><description><c>hsm.pkcs11</c> - PKCS#11 HSM interface</description></item>
    ///   <item><description><c>gpio</c> - GPIO controller (edge/IoT)</description></item>
    ///   <item><description><c>i2c</c> - I2C bus controller</description></item>
    ///   <item><description><c>spi</c> - SPI bus controller</description></item>
    ///   <item><description><c>block</c> - Block storage device</description></item>
    ///   <item><description><c>network</c> - Network adapter</description></item>
    ///   <item><description><c>usb</c> - USB device</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Caching:</strong> Results are cached with a configurable TTL (default 5 minutes).
    /// Repeated queries within the TTL window do not re-probe hardware. Use <see cref="RefreshAsync"/>
    /// to force an immediate hardware re-discovery.
    /// </para>
    /// <para>
    /// <strong>Auto-Refresh:</strong> The registry subscribes to hardware change events from the
    /// underlying <see cref="IHardwareProbe"/>. When devices are added or removed (e.g., USB plug/unplug),
    /// the registry automatically refreshes after a debounce period (default 500ms) to avoid event flooding.
    /// </para>
    /// <para>
    /// <strong>Thread Safety:</strong> All methods are thread-safe. Concurrent reads are supported
    /// without blocking. Cache refresh operations are serialized to prevent race conditions.
    /// </para>
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Platform capability registry interface (HAL-03)")]
    public interface IPlatformCapabilityRegistry : IDisposable
    {
        /// <summary>
        /// Checks if the platform has the specified capability.
        /// </summary>
        /// <param name="capabilityKey">
        /// The dot-separated capability key (e.g., "qat.compression", "gpu.cuda", "tpm2").
        /// Capability keys are case-sensitive.
        /// </param>
        /// <returns>
        /// <c>true</c> if the capability is present; otherwise <c>false</c>.
        /// Returns cached result if within TTL. If the cache is stale, triggers
        /// an asynchronous refresh but still returns the cached result (non-blocking).
        /// </returns>
        bool HasCapability(string capabilityKey);

        /// <summary>
        /// Gets all hardware devices matching the specified type filter.
        /// </summary>
        /// <param name="typeFilter">
        /// Device type flags to filter by. Multiple flags can be combined.
        /// </param>
        /// <returns>
        /// A read-only list of devices matching the filter. Returns an empty list (never null)
        /// if no matching devices are found.
        /// </returns>
        IReadOnlyList<HardwareDevice> GetDevices(HardwareDeviceType typeFilter);

        /// <summary>
        /// Gets all discovered hardware devices regardless of type.
        /// </summary>
        /// <returns>
        /// A read-only list of all discovered devices. Returns an empty list (never null)
        /// if no devices are found.
        /// </returns>
        IReadOnlyList<HardwareDevice> GetAllDevices();

        /// <summary>
        /// Gets all discovered capability keys.
        /// </summary>
        /// <returns>
        /// A read-only list of all capability keys currently in the registry.
        /// Returns an empty list (never null) if no capabilities are discovered.
        /// </returns>
        IReadOnlyList<string> GetCapabilities();

        /// <summary>
        /// Gets the count of devices matching the specified type filter.
        /// </summary>
        /// <param name="typeFilter">
        /// Device type flags to filter by. Multiple flags can be combined.
        /// </param>
        /// <returns>
        /// The number of devices matching the filter. Returns 0 if no matching devices are found.
        /// This method avoids materializing the full device list for simple count queries.
        /// </returns>
        int GetDeviceCount(HardwareDeviceType typeFilter);

        /// <summary>
        /// Forces an immediate hardware re-discovery and rebuilds the capability cache.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <remarks>
        /// This method is called automatically when hardware change events are detected
        /// (after debouncing), but can be invoked manually to force an immediate refresh.
        /// If a refresh is already in progress, this method returns immediately without
        /// starting a duplicate refresh operation.
        /// </remarks>
        /// <returns>A task representing the asynchronous refresh operation.</returns>
        Task RefreshAsync(CancellationToken ct = default);

        /// <summary>
        /// Event fired when platform capabilities change due to hardware being added or removed.
        /// </summary>
        /// <remarks>
        /// This event is raised after a successful <see cref="RefreshAsync"/> operation
        /// when the capability set or device list has changed. The event args provide
        /// detailed delta information (added/removed capabilities and devices).
        /// Event handlers are invoked synchronously after cache update completes.
        /// </remarks>
        event EventHandler<PlatformCapabilityChangedEventArgs>? OnCapabilityChanged;
    }

    /// <summary>
    /// Event arguments for platform capability change notifications.
    /// </summary>
    /// <remarks>
    /// Provides detailed delta information about what changed during a capability refresh.
    /// All collections are read-only and never null (empty if no changes in that category).
    /// </remarks>
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Capability change event args (HAL-03)")]
    public sealed record PlatformCapabilityChangedEventArgs
    {
        /// <summary>
        /// Gets the capabilities that were added during this refresh.
        /// </summary>
        public required IReadOnlyList<string> AddedCapabilities { get; init; }

        /// <summary>
        /// Gets the capabilities that were removed during this refresh.
        /// </summary>
        public required IReadOnlyList<string> RemovedCapabilities { get; init; }

        /// <summary>
        /// Gets the devices that were added during this refresh.
        /// </summary>
        public required IReadOnlyList<HardwareDevice> AddedDevices { get; init; }

        /// <summary>
        /// Gets the devices that were removed during this refresh.
        /// </summary>
        public required IReadOnlyList<HardwareDevice> RemovedDevices { get; init; }
    }
}
