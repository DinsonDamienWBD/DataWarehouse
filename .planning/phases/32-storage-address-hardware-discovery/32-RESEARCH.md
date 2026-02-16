# Phase 32: StorageAddress Abstraction & Hardware Discovery - Research

**Researched:** 2026-02-16
**Domain:** Storage addressing abstraction, hardware discovery, platform detection, dynamic driver loading
**Confidence:** HIGH

## Summary

Phase 32 introduces the foundational `StorageAddress` discriminated union type to replace `string key` / `string path` / `Uri uri` throughout the storage subsystem, and builds a hardware probe/discovery system with a platform capability registry and dynamic driver loading. This is the first phase of v3.0 and all subsequent phases (33-41) depend on it.

The codebase currently uses three distinct addressing patterns: (1) `string key` in the IStorageStrategy / IObjectStorageCore path (the canonical AD-04 model, used by 130+ storage strategies), (2) `Uri uri` in the legacy IStorageProvider path (used in 6 files), and (3) `string path` in IFileSystem and various plugin-level operations. StorageAddress must unify all three while preserving backward compatibility via implicit conversions. The backward compatibility risk is significant -- there are 130+ concrete storage strategies each with ~7 `string key` method signatures in their core methods, plus ~25 SDK files with `string key/path` parameters.

Existing hardware infrastructure is well-designed but purely contractual. The SDK has `IHardwareAccelerator` (two copies: `SDK.Hardware` and `SDK.Primitives.Hardware`), `IHypervisorDetector` with `IBalloonDriver` (in `SDK.Virtualization`), `ITpm2Provider`, `IHsmProvider`, `ISmartNicOffload`, and `HardwareAcceleratorPluginBase`. All are marked `FUTURE: AD-06` with zero implementations. The `PluginAssemblyLoadContext` and `PluginReloadManager` in `SDK.Infrastructure.KernelInfrastructure` provide proven assembly isolation and hot-reload patterns that `IDriverLoader` can reuse.

**Primary recommendation:** Build StorageAddress as a C# record-based discriminated union in `DataWarehouse.SDK/Storage/StorageAddress.cs` with implicit operators from `string` and `Uri`. Introduce it at the SDK contract level first (new overloads, not breaking changes), then layer hardware probe/registry/driver-loader on top using existing infrastructure patterns. Migration of 130+ strategies is the largest risk and should be the final plan.

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Runtime.InteropServices | .NET 9 built-in | RuntimeInformation, OSPlatform for platform detection | Already used across 51 files in the codebase |
| System.Management | .NET 9 built-in | WMI queries on Windows (Win32_PnPEntity, Win32_DiskDrive) | Standard Windows hardware enumeration |
| System.IO.FileSystem | .NET 9 built-in | /sys/class, /proc filesystem reads on Linux | Standard sysfs/procfs access pattern |
| System.Reflection | .NET 9 built-in | Assembly scanning for [StorageDriver] attributes | Already used in ConnectionStrategyRegistry and EncryptionStrategyRegistry |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Device.Gpio | 3.x | GPIO/I2C/SPI bus access (forward-looking for Phase 36) | Only for EDGE variants of StorageAddress (GpioPin, I2cBus, SpiBus) |
| Microsoft.Win32.Registry | .NET 9 built-in | Windows device registry queries | Alternative to WMI for faster device enumeration |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| WMI (System.Management) | P/Invoke to SetupDI API | SetupDI is faster but requires more unsafe code; WMI is simpler and sufficient |
| Custom assembly scanner | MEF (System.ComponentModel.Composition) | MEF adds dependency; existing codebase already does manual assembly scanning |
| Discriminated union via abstract record | OneOf library | External dependency; native C# records with switch expressions are cleaner and zero-dependency |

**Installation:**
No new NuGet packages required. All dependencies are .NET 9 BCL.

## Architecture Patterns

### Recommended Project Structure
```
DataWarehouse.SDK/
├── Storage/
│   ├── StorageAddress.cs              # NEW: Discriminated union type (HAL-01)
│   ├── StorageAddressKind.cs          # NEW: Enum for variant discrimination
│   ├── IObjectStorageCore.cs          # EXISTING: Add StorageAddress overloads
│   ├── PathStorageAdapter.cs          # EXISTING: Update to use StorageAddress
│   └── HybridStoragePluginBase.cs     # EXISTING: No changes needed
├── Hardware/
│   ├── IHardwareProbe.cs              # NEW: Hardware discovery interface (HAL-02)
│   ├── HardwareDevice.cs              # NEW: Device record types
│   ├── WindowsHardwareProbe.cs        # NEW: WMI/SetupDI implementation
│   ├── LinuxHardwareProbe.cs          # NEW: sysfs/procfs implementation
│   ├── MacOsHardwareProbe.cs          # NEW: IOKit implementation
│   ├── IPlatformCapabilityRegistry.cs # NEW: Capability query interface (HAL-03)
│   ├── PlatformCapabilityRegistry.cs  # NEW: Cached implementation
│   ├── IHardwareAcceleration.cs       # EXISTING: Already has QAT/GPU/TPM/HSM contracts
│   └── IDriverLoader.cs              # NEW: Dynamic driver loading (HAL-04)
├── Infrastructure/
│   └── KernelInfrastructure.cs        # EXISTING: PluginAssemblyLoadContext, PluginReloadManager
├── Virtualization/
│   └── IHypervisorSupport.cs          # EXISTING: HypervisorDetector, BalloonDriver contracts
└── Contracts/
    ├── Storage/StorageStrategy.cs      # EXISTING: Add StorageAddress support
    └── Hierarchy/DataPipeline/
        └── StoragePluginBase.cs        # EXISTING: Add StorageAddress overloads
```

### Pattern 1: StorageAddress as Discriminated Union via Abstract Record
**What:** C# record hierarchy with a sealed set of concrete variants
**When to use:** Always -- this is the universal address type
**Example:**
```csharp
// Source: Design pattern derived from codebase analysis (HAL-01 requirements)
namespace DataWarehouse.SDK.Storage;

public enum StorageAddressKind
{
    FilePath,
    BlockDevice,
    NvmeNamespace,
    ObjectKey,
    NetworkEndpoint,
    GpioPin,
    I2cBus,
    SpiBus,
    CustomAddress
}

public abstract record StorageAddress
{
    public abstract StorageAddressKind Kind { get; }

    // Implicit conversions for backward compatibility (HAL-05)
    public static implicit operator StorageAddress(string path)
        => IsObjectKey(path) ? new ObjectKeyAddress(path) : new FilePathAddress(path);

    public static implicit operator StorageAddress(Uri uri)
        => FromUri(uri);

    // Factory methods per HAL-01 spec
    public static StorageAddress FromFilePath(string path) => new FilePathAddress(path);
    public static StorageAddress FromObjectKey(string key) => new ObjectKeyAddress(key);
    public static StorageAddress FromNvme(int nsid, int? controllerId = null)
        => new NvmeNamespaceAddress(nsid, controllerId);
    public static StorageAddress FromBlockDevice(string devicePath)
        => new BlockDeviceAddress(devicePath);
    public static StorageAddress FromNetworkEndpoint(string host, int port, string? scheme = null)
        => new NetworkEndpointAddress(host, port, scheme);
    public static StorageAddress FromGpioPin(int pin, string? boardId = null)
        => new GpioPinAddress(pin, boardId);
    public static StorageAddress FromI2cBus(int busId, int deviceAddress)
        => new I2cBusAddress(busId, deviceAddress);
    public static StorageAddress FromSpiBus(int busId, int chipSelect)
        => new SpiBusAddress(busId, chipSelect);
    public static StorageAddress FromCustom(string scheme, string address)
        => new CustomAddress(scheme, address);

    // Key extraction for backward compat with IObjectStorageCore
    public abstract string ToKey();

    private static bool IsObjectKey(string s) =>
        !s.Contains(Path.DirectorySeparatorChar) &&
        !s.Contains(Path.AltDirectorySeparatorChar) &&
        !Path.IsPathRooted(s);
}

// Concrete variants
public sealed record FilePathAddress(string Path) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.FilePath;
    public override string ToKey() => PathStorageAdapter.NormalizePath(Path);
}

public sealed record ObjectKeyAddress(string Key) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.ObjectKey;
    public override string ToKey() => Key;
}

public sealed record NvmeNamespaceAddress(int NamespaceId, int? ControllerId = null) : StorageAddress
{
    public override StorageAddressKind Kind => StorageAddressKind.NvmeNamespace;
    public override string ToKey() => $"nvme://{ControllerId ?? 0}/ns/{NamespaceId}";
}

// ... (BlockDevice, NetworkEndpoint, GpioPin, I2cBus, SpiBus, Custom follow same pattern)
```

### Pattern 2: Platform-Specific Probe via Factory + OperatingSystem Guards
**What:** Static factory selects correct probe implementation at runtime
**When to use:** IHardwareProbe implementation selection
**Example:**
```csharp
// Source: Existing pattern in PlatformServiceManager.cs and LowLatencyPluginBases.cs
public static IHardwareProbe CreateProbe()
{
    if (OperatingSystem.IsWindows()) return new WindowsHardwareProbe();
    if (OperatingSystem.IsLinux()) return new LinuxHardwareProbe();
    if (OperatingSystem.IsMacOS()) return new MacOsHardwareProbe();
    return new NullHardwareProbe(); // graceful degradation
}
```

### Pattern 3: Backward-Compatible Method Overloading
**What:** Add StorageAddress overloads alongside existing string methods
**When to use:** SDK interface evolution without breaking 130+ strategies
**Example:**
```csharp
// In IStorageStrategy -- add new methods, keep old ones
public interface IStorageStrategy
{
    // Existing (kept for backward compat)
    Task<StorageObjectMetadata> StoreAsync(string key, Stream data, ...);

    // New overload using StorageAddress
    Task<StorageObjectMetadata> StoreAsync(StorageAddress address, Stream data, ...);
}

// In StorageStrategyBase -- default implementation bridges
public abstract class StorageStrategyBase
{
    public virtual Task<StorageObjectMetadata> StoreAsync(StorageAddress address, Stream data, ...)
        => StoreAsync(address.ToKey(), data, ...); // delegates to string version
}
```

### Anti-Patterns to Avoid
- **Breaking the string signature contract:** Do NOT remove or change `string key` method signatures. 130+ strategies override `StoreAsyncCore(string key, ...)`. Changing the base signature would break every one.
- **Putting platform-specific code in shared interfaces:** WMI/sysfs/IOKit code must stay in platform-specific implementations, not in the interface or base class.
- **Making StorageAddress a class instead of a record:** Records provide value equality, immutability, and `with` expressions -- all essential for a value-type address.
- **Over-abstracting the discriminated union:** Don't add `IStorageAddress` interface -- the abstract record IS the abstraction. C# pattern matching (`switch` on `Kind` or `is` patterns) is sufficient.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Assembly isolation for driver loading | Custom AppDomain isolation | `PluginAssemblyLoadContext` (already exists in SDK) | Collectible, handles unload, dependency resolution |
| Hot-reload for drivers | Custom FileSystemWatcher + reload | `PluginReloadManager` (already exists) | Phase/drain/load pattern, state preservation, retry logic |
| Platform detection | Custom CPUID/registry checks | `OperatingSystem.IsWindows/IsLinux/IsMacOS` | Already used in 51 files across codebase |
| URI-to-key translation | Custom URI parser | `PathStorageAdapter.UriToKey()` + `NormalizePath()` | Already handles drive letters, backslashes, path traversal |
| Assembly scanning for attributes | MEF or custom scanner | Existing pattern from `ConnectionStrategyRegistry` | Line 94: `assembly.GetTypes()` with attribute filter |

**Key insight:** The SDK already has most of the infrastructure needed for HAL-02 through HAL-04. The `PluginAssemblyLoadContext`, `PluginReloadManager`, assembly scanning patterns, and platform detection are all proven. The real new work is (1) StorageAddress type design, (2) hardware probe implementations, (3) capability registry, and (4) backward-compatible migration.

## Common Pitfalls

### Pitfall 1: Breaking 130+ Storage Strategy Overrides
**What goes wrong:** Changing `StoreAsyncCore(string key, ...)` signature to `StoreAsyncCore(StorageAddress address, ...)` causes compilation failure in every storage strategy.
**Why it happens:** Each of the 130+ strategies in UltimateStorage overrides `StoreAsyncCore`, `RetrieveAsyncCore`, `DeleteAsyncCore`, `ExistsAsyncCore`, `GetMetadataAsyncCore` -- all with `string key` parameter.
**How to avoid:** Add StorageAddress as NEW overloads with default implementations that delegate to the string version via `ToKey()`. Never remove the string overloads.
**Warning signs:** Compilation errors in plugin projects after SDK changes.

### Pitfall 2: Duplicate Hardware Types
**What goes wrong:** Creating new `AcceleratorType`, `HardwareCapabilities`, etc. that conflict with existing types.
**Why it happens:** The SDK already has TWO sets of hardware types: `SDK.Hardware.AcceleratorType` (flags enum with QAT/GPU/TPM/HSM) and `SDK.Primitives.Hardware.AcceleratorType` (enum with SIMD/AVX/CUDA). Both have `HardwareCapabilities` records with different shapes.
**How to avoid:** Audit existing types first. The new `IHardwareProbe` should return device records that reference existing types, not create new duplicate enums. Use `SDK.Hardware.AcceleratorType` for HAL-related code since it has the right variants (QAT, GPU, TPM, HSM).
**Warning signs:** Ambiguous reference errors, `using` alias gymnastics.

### Pitfall 3: Implicit Conversion Ambiguity
**What goes wrong:** `StorageAddress addr = "my/key"` -- is this a FilePath or an ObjectKey?
**Why it happens:** Many object keys look like relative file paths (e.g., "data/users/profile.json").
**How to avoid:** Define clear heuristics: if the string is path-rooted, contains drive letter, or contains OS-specific separators (`\` on Windows), it's a FilePath. Otherwise, it's an ObjectKey (consistent with current AD-04 key conventions). Document the heuristic and provide explicit factory methods.
**Warning signs:** Tests passing on one OS but failing on another due to path separator differences.

### Pitfall 4: Platform-Specific Code Not Guarded
**What goes wrong:** WMI calls on Linux, sysfs reads on Windows cause PlatformNotSupportedException or FileNotFoundException.
**Why it happens:** Missing `[SupportedOSPlatform]` attributes or missing runtime guards.
**How to avoid:** Use `OperatingSystem.IsWindows()` guards (already standard in codebase -- 51 files). Apply `[SupportedOSPlatform("windows")]` attributes. Provide NullHardwareProbe for unsupported platforms.
**Warning signs:** CA1416 platform compatibility analyzer warnings.

### Pitfall 5: Hot-Plug Event Flooding
**What goes wrong:** USB device plug/unplug generates excessive probe refresh cycles.
**Why it happens:** No debouncing on hardware change events.
**How to avoid:** Debounce hardware change events (e.g., 500ms window). Cache probe results with configurable TTL (matching the IPlatformCapabilityRegistry spec).
**Warning signs:** High CPU usage when plugging/unplugging USB devices rapidly.

## Code Examples

### Existing Storage Strategy Method Signatures (to preserve)
```csharp
// Source: DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs, lines 575-600
// These 7 abstract methods are overridden by EVERY storage strategy.
// DO NOT CHANGE these signatures.

protected abstract Task<StorageObjectMetadata> StoreAsyncCore(string key, Stream data, IDictionary<string, string>? metadata, CancellationToken ct);
protected abstract Task<Stream> RetrieveAsyncCore(string key, CancellationToken ct);
protected abstract Task DeleteAsyncCore(string key, CancellationToken ct);
protected abstract Task<bool> ExistsAsyncCore(string key, CancellationToken ct);
protected abstract IAsyncEnumerable<StorageObjectMetadata> ListAsyncCore(string? prefix, CancellationToken ct);
protected abstract Task<StorageObjectMetadata> GetMetadataAsyncCore(string key, CancellationToken ct);
protected abstract Task<StorageHealthInfo> GetHealthAsyncCore(CancellationToken ct);
protected abstract Task<long?> GetAvailableCapacityAsyncCore(CancellationToken ct);
```

### Existing Assembly Scanning Pattern (reuse for IDriverLoader)
```csharp
// Source: DataWarehouse.SDK/Connectors/ConnectionStrategyRegistry.cs, line 94
// and DataWarehouse.SDK/Contracts/Encryption/EncryptionStrategy.cs, line 1095
var types = assembly.GetTypes()
    .Where(t => t.GetCustomAttribute<StorageDriverAttribute>() != null
             && typeof(IStorageStrategy).IsAssignableFrom(t)
             && !t.IsAbstract);
```

### Existing PluginAssemblyLoadContext Usage (reuse for driver isolation)
```csharp
// Source: DataWarehouse.SDK/Infrastructure/KernelInfrastructure.cs, lines 182-222
var context = new PluginAssemblyLoadContext(pluginId, assemblyPath);
var assembly = context.LoadFromAssemblyPath(assemblyPath);
// To unload: context is collectible, just null the reference and GC
```

### Existing Platform Detection Pattern
```csharp
// Source: DataWarehouse.Shared/Services/PlatformServiceManager.cs, lines 23-66
// Pattern used in 51 files across the codebase
if (OperatingSystem.IsWindows())
{
    // Windows-specific code (WMI, sc.exe, registry)
}
else if (OperatingSystem.IsLinux())
{
    // Linux-specific code (sysfs, systemctl, /proc)
}
else if (OperatingSystem.IsMacOS())
{
    // macOS-specific code (launchctl, IOKit)
}
```

### Existing IStorageProvider (URI-based, 6 files) -- Will Need StorageAddress
```csharp
// Source: DataWarehouse.SDK/Contracts/ProviderInterfaces.cs, lines 28-64
public interface IStorageProvider : IPlugin
{
    string Scheme { get; }
    Task SaveAsync(Uri uri, Stream data);
    Task<Stream> LoadAsync(Uri uri);
    Task DeleteAsync(Uri uri);
    Task<bool> ExistsAsync(Uri uri);
}
```

## Impact Analysis

### StorageAddress Introduction -- Files Affected

**SDK Contract Layer (MUST update for StorageAddress support):**

| File | Impact | Lines | Notes |
|------|--------|-------|-------|
| `SDK/Contracts/Storage/StorageStrategy.cs` | Add overloads | ~766 LOC | IStorageStrategy + StorageStrategyBase -- add StorageAddress overloads with default `ToKey()` delegation |
| `SDK/Storage/IObjectStorageCore.cs` | Add overloads | ~48 LOC | 5 methods need StorageAddress overloads |
| `SDK/Storage/PathStorageAdapter.cs` | Update | ~143 LOC | Accept StorageAddress, extract key via `ToKey()` |
| `SDK/Contracts/Hierarchy/DataPipeline/StoragePluginBase.cs` | Add overloads | ~58 LOC | 6 abstract methods need StorageAddress versions |
| `SDK/Contracts/ProviderInterfaces.cs` | Add overloads | ~65 LOC | IStorageProvider uses Uri -- add StorageAddress bridge |
| `SDK/Contracts/IListableStorage.cs` | Minor | ~25 LOC | StorageListItem uses Uri -- may need StorageAddress variant |
| `SDK/Contracts/IStorageOrchestration.cs` | Add overloads | ~varies | Storage orchestration methods use string key |
| `SDK/Contracts/StorageOrchestratorBase.cs` | Add overloads | ~varies | Orchestrator base with string key methods |
| `SDK/Primitives/Filesystem/IFileSystem.cs` | Add overloads | ~74 LOC | Uses `string path` -- add StorageAddress overloads |
| `SDK/Storage/HybridStoragePluginBase.cs` | Minor | ~675 LOC | StorageConfigBase -- no changes needed initially |
| `SDK/Contracts/TamperProof/*.cs` | Minor | ~varies | Some use `string key` parameters |
| `SDK/Contracts/LowLatencyPluginBases.cs` | Add overloads | ~varies | ReadWithoutCacheAsync/WriteWithoutCacheAsync use `string key` |
| `SDK/Contracts/ICacheableStorage.cs` | Add overloads | ~varies | Cache methods use `string key` |

**Plugin Layer (NOT broken, just optionally updated):**

| Category | File Count | Impact |
|----------|-----------|--------|
| UltimateStorage strategies | 130 files | Zero changes needed initially (base class delegates StorageAddress to string) |
| DatabaseStorage strategies | ~49 files | Zero changes needed initially |
| Other storage-related plugins | ~20 files | Zero changes needed initially |
| Total potential future migration | ~200 files | Can be done incrementally in later phases |

**Summary: ~13 SDK files need updating. Zero plugin files break.**

### Hardware Probe -- New Files
| File | Purpose | Estimated LOC |
|------|---------|---------------|
| `SDK/Hardware/IHardwareProbe.cs` | Interface + HardwareDevice record | ~120 |
| `SDK/Hardware/HardwareDeviceType.cs` | Enum for device types | ~40 |
| `SDK/Hardware/WindowsHardwareProbe.cs` | WMI-based discovery | ~250 |
| `SDK/Hardware/LinuxHardwareProbe.cs` | sysfs-based discovery | ~200 |
| `SDK/Hardware/MacOsHardwareProbe.cs` | IOKit P/Invoke | ~200 |
| `SDK/Hardware/NullHardwareProbe.cs` | Fallback, returns empty | ~30 |
| `SDK/Hardware/HardwareProbeFactory.cs` | Platform-conditional factory | ~30 |
| Total | | ~870 |

### Platform Capability Registry -- New Files
| File | Purpose | Estimated LOC |
|------|---------|---------------|
| `SDK/Hardware/IPlatformCapabilityRegistry.cs` | Interface with query API | ~80 |
| `SDK/Hardware/PlatformCapabilityRegistry.cs` | Cached implementation | ~250 |
| Total | | ~330 |

### Dynamic Driver Loading -- New Files
| File | Purpose | Estimated LOC |
|------|---------|---------------|
| `SDK/Hardware/IDriverLoader.cs` | Interface | ~50 |
| `SDK/Hardware/StorageDriverAttribute.cs` | Attribute for driver discovery | ~30 |
| `SDK/Hardware/DriverLoader.cs` | Assembly scanning + load | ~200 |
| Total | | ~280 |

### Existing Hardware Contracts Assessment

| Contract | Location | LOC | Status | Relevance to Phase 32 |
|----------|----------|-----|--------|----------------------|
| `IHardwareAccelerator` (v1) | `SDK/Hardware/IHardwareAcceleration.cs` | 440 | FUTURE: AD-06, zero implementations | Used by HAL-02 probe output |
| `IHardwareAccelerator` (v2) | `SDK/Primitives/Hardware/IHardwareAccelerator.cs` | 70 | FUTURE: AD-06, zero implementations | Duplicate -- needs consolidation |
| `HardwareCapabilities` (v2) | `SDK/Primitives/Hardware/HardwareTypes.cs` | 141 | Active in LowLatencyPluginBases | Used by existing code, keep |
| `AcceleratorType` (v1) | `SDK/Hardware/IHardwareAcceleration.cs` | Flags enum | FUTURE | QAT/GPU/TPM/HSM flags -- good for HAL |
| `AcceleratorType` (v2) | `SDK/Primitives/Hardware/HardwareTypes.cs` | Enum | Active | SIMD/AVX/CUDA -- complementary |
| `IHypervisorDetector` | `SDK/Virtualization/IHypervisorSupport.cs` | 93 | FUTURE: AD-06 | HAL-02 can delegate to this |
| `IBalloonDriver` | `SDK/Virtualization/IHypervisorSupport.cs` | 35 | FUTURE: AD-06 | HAL-02 probe detects balloon support |
| `ITpm2Provider` | `SDK/Hardware/IHardwareAcceleration.cs` | 45 | FUTURE: AD-06 | HAL-03 registry answers "Is TPM present?" |
| `IHsmProvider` | `SDK/Hardware/IHardwareAcceleration.cs` | 60 | FUTURE: AD-06 | HAL-03 registry answers "Is HSM available?" |
| `HardwareAcceleratorPluginBase` | `SDK/Contracts/HardwareAccelerationPluginBases.cs` | ~300 | FUTURE: AD-06 | Plugin base for accelerator plugins |
| `IStorageOptimizer` | `SDK/Virtualization/IHypervisorSupport.cs` | 25 | FUTURE: AD-06 | Uses `string devicePath` -- candidate for StorageAddress |

**Key finding:** There are two parallel sets of hardware types that should be reconciled. The `SDK.Hardware` set has the right accelerator types for HAL (QAT, GPU, TPM, HSM as flags). The `SDK.Primitives.Hardware` set has more granular CPU instruction types (SIMD, AVX2, AVX512, NEON) and is actively used. Both can coexist but must be clearly namespaced.

### Platform Detection Code Survey

51 files use `OperatingSystem.IsWindows()` / `IsLinux()` / `IsMacOS()`:
- **DataWarehouse.Shared:** PlatformServiceManager.cs (most comprehensive -- handles sc/systemctl/launchctl)
- **DataWarehouse.Launcher:** DataWarehouseHost.cs, Program.cs
- **SDK:** LowLatencyPluginBases.cs (io_uring detection on Linux)
- **Plugins:** 47 files across UltimateStorage, UltimateKeyManagement, UltimateSustainability, FuseDriver, WinFspDriver, etc.

The pattern is well-established and consistent. No custom abstraction needed.

## Recommended Plan Structure

### Plan 32-01: StorageAddress Universal Type (HAL-01, HAL-05 partial)
**Focus:** Design and implement the `StorageAddress` discriminated union with all 9 variants, factory methods, implicit conversions from `string` and `Uri`, `ToKey()` for backward compat.
**Files:** Create `SDK/Storage/StorageAddress.cs`, `SDK/Storage/StorageAddressKind.cs`
**Risk:** LOW -- new type, no breaking changes
**Estimated LOC:** ~300

### Plan 32-02: IHardwareProbe & Platform Implementations (HAL-02)
**Focus:** Create `IHardwareProbe` interface and three platform-specific implementations (Windows/WMI, Linux/sysfs, macOS/IOKit) plus a NullHardwareProbe fallback and factory.
**Files:** Create 7 files in `SDK/Hardware/`
**Risk:** MEDIUM -- platform-specific code needs careful OS guards
**Estimated LOC:** ~870

### Plan 32-03: IPlatformCapabilityRegistry (HAL-03)
**Focus:** Create `IPlatformCapabilityRegistry` interface and cached implementation. Answers runtime queries ("Is QAT available?", "How many NVMe namespaces?"). Auto-refreshes on hardware change events.
**Files:** Create 2 files in `SDK/Hardware/`
**Risk:** LOW -- builds on IHardwareProbe output
**Estimated LOC:** ~330

### Plan 32-04: IDriverLoader & Dynamic Loading (HAL-04)
**Focus:** Create `IDriverLoader` interface, `[StorageDriver]` attribute, and `DriverLoader` implementation using existing `PluginAssemblyLoadContext` patterns. Support hot-plug: watch driver directory, load/unload on changes.
**Files:** Create 3 files in `SDK/Hardware/`
**Risk:** LOW -- reuses existing PluginReloadManager infrastructure
**Estimated LOC:** ~280

### Plan 32-05: SDK Contract Migration & Backward Compatibility (HAL-05)
**Focus:** Add `StorageAddress` overloads to `IStorageStrategy`, `StorageStrategyBase`, `IObjectStorageCore`, `StoragePluginBase`, `IStorageProvider`, and `IFileSystem`. Default implementations delegate to `string` versions via `ToKey()`. Verify all 130+ strategies still compile and 1,039+ tests pass.
**Files:** Update ~13 SDK files
**Risk:** HIGH -- touches the core storage contract surface, must not break anything
**Estimated LOC:** ~200 new lines across existing files

### Wave Ordering
```
Wave 1 (Independent):
  32-01: StorageAddress type (no dependencies)

Wave 2 (Depends on 32-01):
  32-02: IHardwareProbe (uses StorageAddress for device addresses)
  32-05: SDK contract migration (uses StorageAddress type)

Wave 3 (Depends on 32-02):
  32-03: IPlatformCapabilityRegistry (consumes IHardwareProbe output)
  32-04: IDriverLoader (may use probe to detect new hardware)
```

## Risk Assessment

| Risk | Severity | Likelihood | Mitigation |
|------|----------|-----------|------------|
| Breaking 130+ storage strategy signatures | CRITICAL | LOW (if overload pattern used) | Add overloads, never modify existing signatures |
| Test regression (1,039+ tests) | HIGH | LOW | Run full test suite after each plan |
| Platform-specific probe failures | MEDIUM | MEDIUM | NullHardwareProbe fallback, comprehensive OS guards |
| Duplicate hardware types causing confusion | MEDIUM | HIGH | Document which namespace to use, add XML doc cross-refs |
| Implicit conversion ambiguity (path vs key) | LOW | MEDIUM | Clear heuristic, explicit factory methods preferred |
| IOKit P/Invoke on macOS | LOW | LOW | Minimal implementation, macOS is not primary target |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `string key` everywhere | `StorageAddress` discriminated union | Phase 32 (new) | Universal addressing for any storage medium |
| No hardware discovery | `IHardwareProbe` platform-specific probes | Phase 32 (new) | Runtime hardware capability queries |
| Static plugin registration | Dynamic `IDriverLoader` with hot-plug | Phase 32 (new) | Drivers load/unload without restart |
| Separate IStorageProvider (Uri) + IStorageStrategy (key) | Both accept StorageAddress | Phase 32 (new) | Unified addressing across all storage APIs |

**Deprecated/outdated:**
- `IStorageProvider` (URI-based): Will be superseded by StorageAddress but kept for backward compat
- `DedupEntry.StorageAddress` (long): In RAID plugin -- unrelated to new StorageAddress type, field name collision but different semantics (block offset vs address abstraction)

## Open Questions

1. **Hardware type consolidation**
   - What we know: Two `AcceleratorType` enums and two `HardwareCapabilities` records exist in different namespaces
   - What's unclear: Should Phase 32 consolidate them, or leave that for Phase 35 (Hardware Accelerator)?
   - Recommendation: Document the duality, reference both in IHardwareProbe output, defer full consolidation to Phase 35

2. **StorageAddress in StorageObjectMetadata.Key**
   - What we know: `StorageObjectMetadata.Key` is `string` -- used as return value from all storage operations
   - What's unclear: Should the `Key` property become `StorageAddress`? That would be a breaking change on the return type.
   - Recommendation: Keep `Key` as `string` for now, add optional `Address` property of type `StorageAddress?` to `StorageObjectMetadata` via `init`

3. **Scope of IStorageOptimizer.SupportsTrimAsync(string devicePath)**
   - What we know: Uses `string devicePath` -- natural candidate for `StorageAddress.BlockDevice`
   - What's unclear: Is this used anywhere? (Marked FUTURE: AD-06)
   - Recommendation: Add StorageAddress overload in Plan 32-05 since it's in scope and trivial

## Sources

### Primary (HIGH confidence)
- `DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs` -- Full IStorageStrategy interface and StorageStrategyBase (766 LOC)
- `DataWarehouse.SDK/Storage/IObjectStorageCore.cs` -- Canonical key-based storage contract (48 LOC)
- `DataWarehouse.SDK/Storage/PathStorageAdapter.cs` -- URI-to-key translation layer (143 LOC)
- `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/StoragePluginBase.cs` -- Storage plugin base (58 LOC)
- `DataWarehouse.SDK/Hardware/IHardwareAcceleration.cs` -- Existing hardware contracts (440 LOC)
- `DataWarehouse.SDK/Primitives/Hardware/HardwareTypes.cs` -- Hardware capability types (141 LOC)
- `DataWarehouse.SDK/Virtualization/IHypervisorSupport.cs` -- Hypervisor/balloon/optimizer contracts (311 LOC)
- `DataWarehouse.SDK/Infrastructure/KernelInfrastructure.cs` -- PluginAssemblyLoadContext + PluginReloadManager
- `DataWarehouse.SDK/Contracts/ProviderInterfaces.cs` -- IStorageProvider URI-based interface (65 LOC)
- `DataWarehouse.Shared/Services/PlatformServiceManager.cs` -- Platform detection patterns (Windows/Linux/macOS)
- Codebase grep: 132 concrete storage strategy files in UltimateStorage alone

### Secondary (MEDIUM confidence)
- `.planning/REQUIREMENTS-v3.md` -- HAL-01 through HAL-05 specifications
- `.planning/ROADMAP.md` -- Phase 32 description and dependency graph

### Tertiary (LOW confidence)
- None -- all findings verified against codebase

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- all .NET BCL, no external dependencies, verified against existing codebase patterns
- Architecture: HIGH -- patterns derived from existing code (platform detection, assembly loading, strategy hierarchy)
- Impact analysis: HIGH -- verified via grep/glob against actual files (130+ storage strategies, 51 platform detection files, 13 SDK files)
- Pitfalls: HIGH -- derived from actual code conflicts found (duplicate hardware types, 130+ overriding methods)

**Research date:** 2026-02-16
**Valid until:** 2026-03-18 (30 days -- codebase is stable, no external dependency churn)
