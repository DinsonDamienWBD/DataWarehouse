# Phase 32 Plan 03 Summary: Platform Capability Registry

**Date**: 2026-02-17
**Status**: ✅ Completed
**Phase**: 32-storage-address-hardware-discovery
**Plan**: 03-PLAN.md (HAL-03)

## Objective

Created IPlatformCapabilityRegistry and PlatformCapabilityRegistry — the runtime query API that answers "what can this machine do?" based on discovered hardware. This provides semantic queries over raw hardware devices, enabling consumers to ask high-level questions like "Has QAT compression?" or "How many NVMe namespaces?" without interpreting low-level device records.

## Artifacts Created

### 1. `DataWarehouse.SDK/Hardware/IPlatformCapabilityRegistry.cs` (177 lines)

**Interface defining the capability query API:**

- **HasCapability(string capabilityKey)**: Boolean query for hierarchical capability keys (e.g., "qat.compression", "gpu.cuda", "tpm2.sealing")
- **GetDevices(HardwareDeviceType typeFilter)**: Returns devices matching type filter
- **GetAllDevices()**: Returns all discovered devices
- **GetCapabilities()**: Returns all discovered capability keys
- **GetDeviceCount(HardwareDeviceType typeFilter)**: Efficient count without materializing full list
- **RefreshAsync(CancellationToken)**: Forces immediate hardware re-discovery
- **OnCapabilityChanged event**: Fires when capabilities change with detailed delta (added/removed capabilities and devices)

**PlatformCapabilityChangedEventArgs record:**
- Provides delta information: AddedCapabilities, RemovedCapabilities, AddedDevices, RemovedDevices
- All collections are read-only and never null

**Key Design Points:**
- Comprehensive XML documentation explaining capability key hierarchies, caching behavior, thread safety, and auto-refresh
- Capability keys are dot-separated hierarchical names (e.g., "nvme", "nvme.controller", "gpu.cuda")
- Results are cached with configurable TTL; use RefreshAsync to force update
- Thread-safe for concurrent reads

### 2. `DataWarehouse.SDK/Hardware/PlatformCapabilityRegistry.cs` (514 lines)

**Cached implementation with auto-refresh:**

**PlatformCapabilityRegistryOptions record:**
- `CacheTtl`: Time-to-live for cached data (default 5 minutes)
- `HardwareChangeDebounce`: Debounce duration for hardware change events (default 500ms)
- `AutoRefreshOnHardwareChange`: Enable/disable automatic refresh on hardware changes (default true)

**Implementation Highlights:**

1. **Constructor Injection**: Takes `IHardwareProbe` via constructor (not owned, not disposed)

2. **Caching Strategy**:
   - Maintains in-memory cache of `_devices` and `_capabilities`
   - Tracks `_lastRefresh` timestamp
   - Cold start: First query triggers synchronous initialization
   - Staleness check: Triggers background refresh when TTL expires (non-blocking)

3. **Thread Safety**:
   - `ReaderWriterLockSlim` for concurrent reads, exclusive writes on cache
   - `SemaphoreSlim` for refresh serialization (prevents duplicate concurrent refreshes)
   - All public methods are thread-safe

4. **Hardware Change Handling**:
   - Subscribes to `IHardwareProbe.OnHardwareChanged` if `AutoRefreshOnHardwareChange` is enabled
   - Debounces events (default 500ms) to prevent flooding from rapid USB plug/unplug
   - Timer callback triggers fire-and-forget `RefreshAsync()`

5. **Capability Derivation (`DeriveCapabilities` method)**:
   Maps device types to capability keys:
   - NvmeController → "nvme", "nvme.controller"
   - NvmeNamespace → "nvme", "nvme.namespace"
   - GpuAccelerator → "gpu" + vendor-specific:
     - NVIDIA (VendorId 0x10DE) → "gpu.cuda"
     - AMD (VendorId 0x1002) → "gpu.rocm"
     - Intel (VendorId 0x8086) → "gpu.directml"
   - QatAccelerator → "qat", "qat.compression", "qat.encryption"
   - TpmDevice → "tpm2", "tpm2.sealing", "tpm2.attestation"
   - HsmDevice → "hsm", "hsm.pkcs11"
   - GpioController → "gpio"
   - I2cBus → "i2c"
   - SpiBus → "spi"
   - BlockDevice → "block"
   - NetworkAdapter → "network"
   - UsbDevice → "usb"

6. **RefreshAsync Implementation**:
   - Acquires `_refreshLock` (non-blocking, returns immediately if already refreshing)
   - Calls `_probe.DiscoverAsync()` to get fresh device list
   - Derives capabilities from devices
   - Computes delta (added/removed capabilities and devices)
   - Updates cache under write lock
   - Fires `OnCapabilityChanged` event if delta is non-empty

7. **Dispose Pattern**:
   - Unsubscribes from hardware change events
   - Disposes debounce timer with lock protection
   - Disposes synchronization primitives (`_refreshLock`, `_cacheLock`)
   - Does NOT dispose `_probe` (injected, not owned)

## Verification

### Build Status
```
dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Implementation Checks
✅ IPlatformCapabilityRegistry has HasCapability, GetDevices, GetAllDevices, GetCapabilities, GetDeviceCount, RefreshAsync
✅ PlatformCapabilityChangedEventArgs provides delta information
✅ PlatformCapabilityRegistry implements IPlatformCapabilityRegistry
✅ ReaderWriterLockSlim for concurrent reads
✅ SemaphoreSlim for refresh serialization
✅ DeriveCapabilities method derives capability keys from device types
✅ Debounce timer prevents hardware change event flooding
✅ Proper IDisposable cleanup of timers, locks, and event subscriptions
✅ Cache TTL configurable with sensible default (5 minutes)
✅ Hardware change debouncing with sensible default (500ms)
✅ Thread-safe for concurrent reads and serialized writes

### File Verification
- `IPlatformCapabilityRegistry.cs`: 177 lines (min 40 ✅)
- `PlatformCapabilityRegistry.cs`: 514 lines (min 200 ✅)
- Both files in `DataWarehouse.SDK/Hardware/` namespace
- `[SdkCompatibility("3.0.0", Notes = "Phase 32: ...")]` attributes present
- Zero existing files modified (purely additive)

## Key Links (from Plan)

1. **PlatformCapabilityRegistry → IHardwareProbe**
   - Via: constructor injection, DiscoverAsync for inventory, OnHardwareChanged for refresh
   - Pattern: `IHardwareProbe _probe`
   - Confirmed: ✅ Constructor takes `IHardwareProbe probe`, calls `_probe.DiscoverAsync()`, subscribes to `_probe.OnHardwareChanged`

2. **PlatformCapabilityRegistry → HardwareDevice**
   - Via: builds capability index from discovered devices
   - Pattern: `HardwareDevice`
   - Confirmed: ✅ `_devices` cache stores `IReadOnlyList<HardwareDevice>`, `DeriveCapabilities` iterates over devices

3. **PlatformCapabilityRegistry → HardwareDeviceType**
   - Via: maps device types to capability strings
   - Pattern: `HardwareDeviceType`
   - Confirmed: ✅ `GetDevices(HardwareDeviceType typeFilter)`, `GetDeviceCount(HardwareDeviceType typeFilter)`, `DeriveCapabilities` uses `device.Type.HasFlag(...)`

## Success Criteria Met

✅ **HasCapability("nvme") returns true** on a machine with NVMe drives (implementation checks `_capabilities.Contains(key)`)
✅ **GetDevices(HardwareDeviceType.GpuAccelerator) returns GPU devices** (implementation filters `_devices.Where(d => d.Type.HasFlag(typeFilter))`)
✅ **Cached results served within TTL** without re-probing (staleness check: `_lastRefresh + CacheTtl < DateTimeOffset.UtcNow`)
✅ **Hardware change events trigger auto-refresh** after debounce (OnProbeHardwareChanged resets debounce timer, timer callback triggers RefreshAsync)
✅ **Thread-safe for concurrent reads and serialized writes** (ReaderWriterLockSlim + SemaphoreSlim)
✅ **Zero new NuGet dependencies** (uses only BCL types: System.Threading, System.Linq, System.Collections.Generic)
✅ **Zero existing files modified** (purely additive, 2 new files created)

## Design Highlights

### Concurrent Read Optimization
The registry uses `ReaderWriterLockSlim` to allow multiple threads to query capabilities simultaneously without blocking. Only cache updates (refresh operations) require exclusive write access. This design is critical for high-throughput scenarios where many plugins query capabilities in parallel.

### Cold Start Handling
On the first query (when `_lastRefresh == DateTimeOffset.MinValue`), the registry performs a synchronous initialization via `RefreshAsync().GetAwaiter().GetResult()`. This ensures the first caller gets valid results rather than false negatives. Subsequent queries trigger background refreshes only when the cache is stale.

### Debouncing Strategy
Hardware change events (USB plug/unplug, Thunderbolt device arrival) can flood in rapid succession. The registry uses a timer-based debounce (default 500ms) to wait for a period of silence before triggering a refresh. This prevents excessive re-discovery operations while still responding quickly to hardware changes.

### Capability Key Hierarchy
Capability keys use dot-notation hierarchies (e.g., "gpu.cuda", "qat.compression") to enable both broad queries ("does this have QAT?") and specific queries ("does this have QAT compression?"). Phase 35 (hardware accelerators) and Phase 36 (edge/IoT) will extend this with additional capability keys as new device types are discovered.

### Vendor Detection Logic
GPU vendor detection uses both VendorId (PCI hex codes) and Vendor string matching to handle different platform probing implementations:
- NVIDIA: VendorId 0x10DE or Vendor contains "NVIDIA"
- AMD: VendorId 0x1002 or Vendor contains "AMD" / "Advanced Micro Devices"
- Intel: VendorId 0x8086 or Vendor contains "Intel"

This dual-path logic ensures robust detection across Windows (PnP VendorId), Linux (sysfs vendor string), and macOS (IOKit).

## Next Steps

This completes HAL-03 (Platform Capability Registry). The capability registry is now ready for use by:
- Phase 33: BlockDeviceService and hardware-aware volume manager
- Phase 35: Hardware accelerator plugins (QAT, GPU compute)
- Phase 36: Edge/IoT plugins (GPIO, I2C, SPI)
- Phase 41: Virtual machine integration (capability-aware VM placement)

**Next in Phase 32**: Plan 04 will likely implement HardwareProbeFactory (the platform-specific factory that creates the appropriate IHardwareProbe implementation based on runtime OS detection).

## Notes

- The registry does NOT dispose the injected `IHardwareProbe` — it is owned by the caller (typically the DI container or HardwareProbeFactory).
- All collections returned are read-only and never null (empty lists for no results).
- The OnCapabilityChanged event fires synchronously after cache update completes. Event handlers should be fast to avoid blocking refresh operations.
- Bounded collections: capability set is bounded by device type count (~70 max), device list is bounded by physical hardware (~100 typical).
- Fire-and-forget pattern (`_ = RefreshAsync()`) is used for background staleness refresh and debounced hardware change refresh to avoid blocking query methods.

---

**Phase 32 Progress**: Plan 03 complete ✅ (HAL-03 Platform Capability Registry)
