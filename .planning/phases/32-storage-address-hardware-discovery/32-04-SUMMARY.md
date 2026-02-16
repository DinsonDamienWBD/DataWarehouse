# Phase 32 Plan 04 Summary: Driver Loading Infrastructure

**Plan**: `.planning/phases/32-storage-address-hardware-discovery/32-04-PLAN.md`
**Status**: ✅ Complete
**Date**: 2026-02-17

## Overview

Created the dynamic storage driver loading infrastructure (HAL-04) that enables runtime discovery, loading, and unloading of storage drivers without recompilation. This system uses assembly isolation for driver safety and supports hot-plug scenarios through directory watching and hardware change events.

## Files Created

### 1. StorageDriverAttribute.cs (3,786 bytes)
**Location**: `DataWarehouse.SDK/Hardware/StorageDriverAttribute.cs`

Custom attribute for marking storage driver types as discoverable:
- **Name** (required): Human-readable driver name
- **SupportedDevices**: Hardware device type flags (from `HardwareDeviceType` enum)
- **Version**: Optional version string
- **Description**: Optional description
- **MinimumPlatform**: Optional minimum OS version
- **AutoLoad**: Whether to auto-load when supported hardware is detected (default: true)

Example usage:
```csharp
[StorageDriver("Samsung NVMe Driver",
    SupportedDevices = HardwareDeviceType.NvmeController | HardwareDeviceType.NvmeNamespace,
    Version = "1.0.0",
    AutoLoad = true)]
public class SamsungNvmeDriver : IStorageStrategy { }
```

### 2. IDriverLoader.cs (10,225 bytes)
**Location**: `DataWarehouse.SDK/Hardware/IDriverLoader.cs`

Interface for dynamic driver lifecycle management:

**Core Methods**:
- `ScanAsync(string assemblyPath)` — Discover drivers in a single assembly without loading
- `ScanDirectoryAsync(string directoryPath)` — Scan all assemblies in a directory
- `LoadAsync(string assemblyPath, string typeName)` — Load driver in isolated context
- `UnloadAsync(DriverHandle handle)` — Unload driver and release assembly context
- `GetLoadedDrivers()` — Query all loaded drivers
- `GetDriverAsync(string driverName)` — Find loaded driver by name

**Events**:
- `OnDriverLoaded` — Fired when driver successfully loads
- `OnDriverUnloaded` — Fired when driver successfully unloads

**Supporting Types**:
- `DriverInfo` record — Metadata about discovered drivers (assembly path, type name, attribute properties)
- `DriverHandle` class — Handle to loaded driver (unique ID, metadata, loaded type, load status)
- `DriverEventArgs` record — Event data for driver lifecycle events
- `DriverEventType` enum — Event types: Loaded, Unloaded, LoadFailed, UnloadFailed

### 3. DriverLoader.cs (19,380 bytes)
**Location**: `DataWarehouse.SDK/Hardware/DriverLoader.cs`

Full implementation of `IDriverLoader` with advanced features:

**Core Features**:
- **Assembly Isolation**: Uses existing `PluginAssemblyLoadContext` (collectible) for safe driver loading
- **Reflection-Only Scanning**: Temporary load contexts for discovery without permanently loading assemblies
- **Thread-Safe Operations**: `SemaphoreSlim` serializes load/unload, `ConcurrentDictionary` for driver registry
- **Bounded Loading**: Configurable maximum driver count (default: 100)
- **Graceful Unloading**: Configurable grace period for driver shutdown (default: 5 seconds)

**Hot-Plug Support**:
1. **Directory Watching** (`FileSystemWatcher`):
   - Monitors configured driver directory for new DLLs
   - Auto-loads drivers with `AutoLoad=true` when DLLs are added
   - Auto-unloads drivers when DLLs are removed
   - 500ms debounce for file write completion

2. **Hardware Change Triggered**:
   - Subscribes to `IHardwareProbe.OnHardwareChanged` events
   - When hardware added: scans for matching driver by `SupportedDevices` flags
   - Auto-loads first matching driver with `AutoLoad=true`
   - Conservative unloading: only unloads if no matching hardware remains

**Configuration** (`DriverLoaderOptions`):
- `DriverDirectory` — Path to driver directory for hot-plug
- `EnableHotPlug` — Enable directory watching
- `AutoLoadOnHardwareChange` — Enable hardware event auto-loading
- `MaxLoadedDrivers` — Maximum concurrent drivers
- `UnloadGracePeriod` — Timeout for driver shutdown

**Error Handling**:
- All operations wrapped in try/catch
- Failed assemblies during directory scans are logged and skipped
- Events fire with `LoadFailed`/`UnloadFailed` on errors
- Hot-plug failures are non-fatal (logged but don't block other operations)

## Technical Implementation Details

### Assembly Scanning Pattern
Reuses existing `ConnectionStrategyRegistry` pattern:
```csharp
assembly.GetTypes()
    .Where(t => !t.IsAbstract && t.IsClass &&
                t.GetCustomAttribute<StorageDriverAttribute>() != null)
```

Handles `ReflectionTypeLoadException` gracefully by returning successfully loaded types.

### Assembly Isolation Pattern
Reuses existing `PluginAssemblyLoadContext` from `KernelInfrastructure.cs`:
```csharp
var context = new PluginAssemblyLoadContext(handleId, assemblyPath);
var assembly = context.LoadFromAssemblyPath(assemblyPath);
// ... use driver ...
context.Unload();  // Triggers collectible cleanup
```

### Hardware Integration
Integrates with `IHardwareProbe` (from Phase 32 Plan 02):
```csharp
_probe.OnHardwareChanged += OnHardwareChangedHandler;

void OnHardwareChangedHandler(object? sender, HardwareChangeEventArgs e)
{
    if (e.ChangeType == HardwareChangeType.Added)
    {
        // Find and load matching driver
        var matching = drivers.FirstOrDefault(d =>
            (d.SupportedDevices & e.Device.Type) != 0);
    }
}
```

## Verification Results

✅ **Build**: `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` — 0 errors, 0 warnings
✅ **Files Exist**: All three files created in `DataWarehouse.SDK/Hardware/`
✅ **Attribute Properties**: Name, SupportedDevices, AutoLoad, Version, Description, MinimumPlatform
✅ **Interface Methods**: ScanAsync, ScanDirectoryAsync, LoadAsync, UnloadAsync, GetLoadedDrivers, GetDriverAsync
✅ **Assembly Isolation**: PluginAssemblyLoadContext reused (4 occurrences)
✅ **Attribute Scanning**: GetCustomAttribute<StorageDriverAttribute> pattern (4 occurrences)
✅ **Hot-Plug Directory**: FileSystemWatcher implementation (2 occurrences)
✅ **Bounded Loading**: MaxLoadedDrivers enforcement (3 occurrences)
✅ **Hardware Integration**: Subscribes to IHardwareProbe.OnHardwareChanged
✅ **Thread Safety**: SemaphoreSlim + ConcurrentDictionary

## Success Criteria Met

- ✅ [StorageDriver] attribute marks types for discovery with metadata (name, supported devices, auto-load)
- ✅ ScanAsync discovers attributed types in an assembly without permanently loading it
- ✅ LoadAsync loads a driver in an isolated PluginAssemblyLoadContext
- ✅ UnloadAsync releases the AssemblyLoadContext for GC collection
- ✅ Hot-plug directory watching auto-loads new DLLs and unloads removed DLLs
- ✅ Reuses existing SDK infrastructure: PluginAssemblyLoadContext, assembly scanning pattern
- ✅ Zero new NuGet dependencies
- ✅ Zero existing files modified

## Architecture Notes

### Reuse of Existing Patterns
This implementation extensively reuses proven SDK infrastructure:
1. **PluginAssemblyLoadContext** (from `KernelInfrastructure.cs`) — Collectible assembly loading
2. **Assembly Scanning** (from `ConnectionStrategyRegistry.cs`) — Attribute-based type discovery
3. **Event Pattern** (from `IHardwareProbe`) — Hardware change notifications

### Design Decisions
1. **Temporary Contexts for Scanning**: Use collectible load contexts even for scanning to avoid file locking
2. **Conservative Auto-Unload**: Only unload drivers when ALL matching hardware is removed
3. **Bounded Driver Count**: Prevent resource exhaustion from runaway driver loading
4. **Graceful Shutdown**: Configurable timeout before forced unload
5. **Non-Fatal Hot-Plug**: Individual driver load failures don't stop hot-plug monitoring

### Integration Points
- **IHardwareProbe** — Subscribes to hardware change events for auto-loading
- **PluginAssemblyLoadContext** — Provides assembly isolation and collectible unloading
- **StorageDriverAttribute** — Discovery metadata for driver scanning
- **Future IStorageStrategy** — Drivers will implement this interface (referenced in docs)

## Next Steps

This completes the driver loading infrastructure (HAL-04). The next plan (32-05) will likely implement:
- `IStorageDriver` interface that drivers must implement
- Driver instance lifecycle management (create, initialize, dispose)
- Driver capability queries and hardware binding

## Files Modified

**None** — All work was purely additive as specified.

## Dependencies Satisfied

- Phase 32 Plan 02 (IHardwareProbe, HardwareDevice, HardwareDeviceType) — Used for hardware change integration
- Existing SDK infrastructure (PluginAssemblyLoadContext, ConnectionStrategyRegistry patterns)

---

**Implementation Time**: ~5 minutes
**Build Time**: ~10 seconds
**Total Lines**: 33,391 bytes across 3 files
