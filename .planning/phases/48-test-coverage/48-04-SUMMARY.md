---
phase: "48"
plan: "04"
subsystem: "test-infrastructure"
tags: [cross-platform, platform-specific, hardware-probes, os-detection]
dependency-graph:
  requires: [48-01-unit-test-gaps]
  provides: [cross-platform-test-gap-map]
  affects: [sdk-hardware, plugins-filesystem, plugins-sustainability]
tech-stack:
  added: []
  patterns: [runtime-detection, conditional-compilation, p-invoke]
key-files:
  created: []
  modified: []
decisions:
  - "Analysis only - no code modifications"
metrics:
  completed: "2026-02-17"
---

# Phase 48 Plan 04: Cross-Platform Test Gap Analysis

**One-liner:** 53 files use RuntimeInformation/OS detection and 41 files use hardware probes (WMI, sysfs, system_profiler), but zero cross-platform tests exist -- all tests run only on the host OS with NullHardwareProbe.

## Platform-Specific Code Inventory

### OS Detection Patterns (53 files)

Files using `RuntimeInformation.IsOSPlatform`, `#if WINDOWS`, `PlatformID`, or `Environment.OSVersion`:

#### SDK Hardware Layer (8 files)
| File | Pattern | What It Does |
|------|---------|-------------|
| `SDK/Hardware/WindowsHardwareProbe.cs` | Windows-only class | WMI queries for device discovery |
| `SDK/Hardware/LinuxHardwareProbe.cs` | Linux-only class | /sys/, /proc/ parsing for devices |
| `SDK/Hardware/MacOsHardwareProbe.cs` | macOS-only class | system_profiler parsing |
| `SDK/Hardware/HardwareProbeFactory.cs` | OS switch | Creates platform-specific probe |
| `SDK/Hardware/Accelerators/QatAccelerator.cs` | OS detection | QAT hardware access |
| `SDK/Hardware/Accelerators/Tpm2Provider.cs` | OS detection | TPM2 access (Windows vs Linux) |
| `SDK/Hardware/NVMe/NvmePassthrough.cs` | OS detection | NVMe passthrough (ioctl vs DeviceIoControl) |
| `SDK/Hardware/Memory/NumaAllocator.cs` | OS detection | NUMA memory allocation |

#### SDK Deployment/Edge (5 files)
| File | Pattern | What It Does |
|------|---------|-------------|
| `SDK/Deployment/SpdkBindingValidator.cs` | OS detection | SPDK availability check |
| `SDK/Deployment/EdgeDetector.cs` | OS detection | Edge device detection |
| `SDK/Deployment/FilesystemDetector.cs` | OS detection | Filesystem type detection |
| `SDK/Edge/Bus/GpioBusController.cs` | Linux-specific | GPIO pin access via /sys/class/gpio |
| `SDK/Edge/Bus/BusControllerFactory.cs` | OS detection | Bus controller creation |

#### Plugin: UltimateKeyManagement (8 files)
| File | Pattern | What It Does |
|------|---------|-------------|
| `Strategies/Platform/WindowsCredManagerStrategy.cs` | Windows-only | Windows Credential Manager |
| `Strategies/Platform/MacOsKeychainStrategy.cs` | macOS-only | macOS Keychain |
| `Strategies/Platform/LinuxSecretServiceStrategy.cs` | Linux-only | D-Bus Secret Service |
| `Strategies/Platform/SshAgentStrategy.cs` | OS detection | SSH agent communication |
| `Strategies/Hardware/TpmStrategy.cs` | OS detection | TPM hardware access |
| `Strategies/Hardware/NitrokeyStrategy.cs` | OS detection | Nitrokey USB device |
| `Strategies/Container/DockerSecretsStrategy.cs` | Linux-specific | Docker secrets path |
| `Strategies/Local/FileKeyStoreStrategy.cs` | OS detection | File permissions |

#### Plugin: UltimateFilesystem (4 files)
| File | Pattern | What It Does |
|------|---------|-------------|
| `Strategies/FormatStrategies.cs` | OS detection | Filesystem formatting (mkfs vs format) |
| `Strategies/NetworkFilesystemStrategies.cs` | OS detection | NFS/SMB mounting |
| `Strategies/DetectionStrategies.cs` | OS detection | Filesystem detection |
| `UltimateFilesystemPlugin.cs` | OS detection | Filesystem operations |

#### Plugin: FuseDriver (6 files - Linux/macOS only)
| File | Pattern | What It Does |
|------|---------|-------------|
| `FuseFileSystem.cs` | Linux/macOS only | FUSE filesystem mounting |
| `SystemdIntegration.cs` | Linux-only | systemd service management |
| `OverlayfsBackend.cs` | Linux-only | Overlay filesystem |
| `NamespaceManager.cs` | Linux-only | Linux namespaces |
| `MacOsSpecific.cs` | macOS-only | macFUSE integration |
| `LinuxSpecific.cs` | Linux-only | libfuse integration |

#### Plugin: WinFspDriver (1 file - Windows only)
| File | Pattern | What It Does |
|------|---------|-------------|
| `WinFspDriverPlugin.cs` | Windows-only | WinFsp filesystem driver |

#### Plugin: UltimateSustainability (7 files)
| File | Pattern | What It Does |
|------|---------|-------------|
| `Strategies/EnergyOptimization/GpuPowerManagementStrategy.cs` | OS detection | GPU power management |
| `Strategies/EnergyOptimization/CpuFrequencyScalingStrategy.cs` | OS detection | CPU freq via sysfs/WMI |
| `Strategies/EnergyOptimization/SleepStatesStrategy.cs` | OS detection | Sleep state management |
| `Strategies/EnergyOptimization/PowerCappingStrategy.cs` | OS detection | RAPL/WMI power caps |
| `Strategies/EnergyOptimization/WorkloadConsolidationStrategy.cs` | OS detection | Process affinity |
| `Strategies/BatteryAwareness/BatteryLevelMonitoringStrategy.cs` | OS detection | Battery via sysfs/WMI |
| `Strategies/ThermalManagement/TemperatureMonitoringStrategy.cs` | OS detection | Thermal via sysfs/WMI |

#### Other Plugins (14 files)
| Plugin | Files | Pattern |
|--------|-------|---------|
| AirGapBridge | 1 | Device sentinel OS detection |
| AedsCore | 1 | Notification plugin |
| UltimateAccessControl | 2 | Kerberos, Evil Maid protection |
| UltimateConnector | 2 | Zero Trust mesh, Battery handshake |
| UltimateCompute | 1 | AppArmor sandbox (Linux-only) |
| UltimateStorage | 2 | PMEM, SCM, NFS strategies |
| UltimateEncryption | 1 | Transit encryption |
| UniversalObservability | 3 | Error tracking, container resources |
| UltimateDatabaseProtocol | 2 | MongoDB, specialized protocols |
| UltimateResourceManager | 1 | Power management strategies |

### Conditional Compilation (#if directives)

Only 6 conditional compilation directives across 3 files:
- `DataWarehouse.GUI/MauiProgram.cs` - platform-specific MAUI config
- `UltimateDatabaseStorage/Strategies/NoSQL/CouchDbStorageStrategy.cs`
- `UltimateIntelligence/Strategies/ConnectorIntegration/INTEGRATION_EXAMPLE.cs`

**Note:** Most platform branching uses runtime `if` statements with `RuntimeInformation.IsOSPlatform()` rather than compile-time `#if` directives. This means all platform code compiles on all platforms but only executes on the target OS.

## Hardware Probe Analysis (41 files)

### SDK Hardware Probes

| Probe | OS | Discovery Method | Tests |
|-------|-----|-----------------|-------|
| `WindowsHardwareProbe` | Windows | WMI (ManagementObjectSearcher) | 0 |
| `LinuxHardwareProbe` | Linux | /sys/class/, /proc/ filesystem | 0 |
| `MacOsHardwareProbe` | macOS | system_profiler CLI | 0 |
| `NullHardwareProbe` | All | Returns empty (fallback) | 1 (in V3ComponentTests) |
| `HardwareProbeFactory` | All | OS switch to create probe | 0 |

### Hardware Access Patterns (P/Invoke, ioctl, WMI)

| Pattern | Files | What It Does |
|---------|-------|-------------|
| WMI queries | ~8 | Windows device/power/thermal discovery |
| /sys/ filesystem reads | ~12 | Linux device/power/thermal/GPIO |
| system_profiler CLI | ~3 | macOS device discovery |
| P/Invoke (native calls) | ~5 | NVMe passthrough, TPM, NUMA |
| Process.Start (CLI tools) | ~10 | mkfs, mount, systemctl, etc. |

## Cross-Platform Test Coverage: ZERO

### What Exists
- `V3ComponentTests.NullHardwareProbe_ReturnsEmptyDiscoveryResults()` - tests the null/fallback probe only
- `V3ComponentTests.PlatformCapabilityRegistry_CanBeInstantiated()` - instantiation with null probe
- `V3ComponentTests.HardwareDevice_CanBeConstructed()` - type construction only
- `V3ComponentTests.HardwareDeviceType_FlagsEnumHasExpectedValues()` - enum values only

### What Does NOT Exist
- Zero tests for `WindowsHardwareProbe` actual discovery
- Zero tests for `LinuxHardwareProbe` actual discovery
- Zero tests for `MacOsHardwareProbe` actual discovery
- Zero tests for `HardwareProbeFactory` OS routing
- Zero tests for any platform-specific strategy behavior
- Zero tests verifying graceful degradation when running on wrong OS
- Zero tests for P/Invoke failure handling
- Zero tests for missing CLI tool handling (mkfs, system_profiler, etc.)

## Platform-Specific Code Paths Lacking Tests

### Critical (Data Integrity Impact)

| # | Code Path | Platform | Risk | Tests |
|---|-----------|----------|------|-------|
| 1 | NVMe passthrough (ioctl/DeviceIoControl) | Win/Linux | Data corruption on wrong ioctl | 0 |
| 2 | PMEM/SCM storage strategy | Linux | Persistent memory corruption | 0 |
| 3 | Filesystem format strategies | Win/Linux/Mac | Wrong format command | 0 |
| 4 | TPM2 key storage | Win/Linux | Key inaccessible on wrong OS | 0 |
| 5 | WORM hardware integration | Win/Linux | Compliance failure | 0 |

### High (Security Impact)

| # | Code Path | Platform | Risk | Tests |
|---|-----------|----------|------|-------|
| 6 | Windows Credential Manager | Windows | Credential leak | 0 |
| 7 | macOS Keychain | macOS | Credential leak | 0 |
| 8 | Linux Secret Service (D-Bus) | Linux | Credential leak | 0 |
| 9 | AppArmor sandbox | Linux | Sandbox escape | 0 |
| 10 | BitLocker integration | Windows | Encryption bypass | 0 |

### Medium (Operations Impact)

| # | Code Path | Platform | Risk | Tests |
|---|-----------|----------|------|-------|
| 11 | CPU frequency scaling (sysfs vs WMI) | Win/Linux | Wrong power state | 0 |
| 12 | Battery monitoring (sysfs vs WMI) | Win/Linux | Wrong battery level | 0 |
| 13 | Temperature monitoring | Win/Linux | Wrong thermal reading | 0 |
| 14 | FUSE filesystem mounting | Linux/Mac | Mount failure | 0 |
| 15 | WinFsp driver | Windows | Driver load failure | 0 |
| 16 | GPIO controller | Linux | GPIO access failure | 0 |
| 17 | Systemd integration | Linux | Service management failure | 0 |
| 18 | NUMA memory allocation | Win/Linux | Memory affinity failure | 0 |
| 19 | Overlay filesystem | Linux | Layer corruption | 0 |
| 20 | NFS/SMB mounting | Win/Linux/Mac | Network share failure | 0 |

## Testing Strategy Challenges

1. **Runtime branching:** Since most platform code uses `RuntimeInformation.IsOSPlatform()` rather than `#if`, all code compiles everywhere but only the host OS branch executes during tests
2. **Hardware dependencies:** Many paths require actual hardware (NVMe, TPM, GPU) that may not exist in CI
3. **Elevated privileges:** Filesystem mounting, GPIO access, kernel module loading require root/admin
4. **CI matrix cost:** Testing on Windows + Linux + macOS triples CI time and cost

## Recommendations

1. **Immediate (P0):** Add abstraction interfaces for all OS-specific operations (many already use strategy pattern)
2. **Immediate (P0):** Create mock/stub implementations for each hardware probe that return realistic data
3. **Short-term (P1):** Add tests that verify graceful degradation when platform detection returns "unsupported"
4. **Short-term (P1):** Add tests for HardwareProbeFactory routing logic
5. **Medium-term (P2):** Set up CI matrix with Windows + Linux runners (macOS optional due to cost)
6. **Medium-term (P2):** Create virtualized hardware test fixtures (mock /sys/ filesystem, mock WMI)
7. **Infrastructure:** Add `[Trait("Platform", "Windows")]` / `[Trait("Platform", "Linux")]` conventions for platform-specific tests
8. **Infrastructure:** Consider Docker-based Linux testing from Windows CI runners for /sys/ and /proc/ tests

## Self-Check: PASSED
- 53 files with OS detection confirmed via grep
- 41 files with hardware probes confirmed via grep
- Zero cross-platform tests confirmed (only NullHardwareProbe tested)
- Conditional compilation count verified: 6 directives in 3 files
