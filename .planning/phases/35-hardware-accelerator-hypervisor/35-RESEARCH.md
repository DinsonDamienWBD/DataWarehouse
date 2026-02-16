# Phase 35: Hardware Accelerator & Hypervisor Integration — Research

**Phase Goal**: Integrate with real hardware accelerators (Intel QAT, GPU via CUDA/ROCm), security hardware (TPM2, HSM), hypervisor introspection (VMware, Hyper-V, KVM detection), and bare-metal optimizations (NUMA-aware allocation, NVMe command passthrough).

**Dependencies**: Phase 32 (hardware probe/discovery, platform capability registry)

**Requirements**: HW-01 through HW-07

---

## Key Findings

### 1. Hardware Acceleration Strategy (HW-01, HW-02)

All hardware accelerator implementations follow the **optional acceleration pattern**:

1. **Detect** hardware via Phase 32 `IPlatformCapabilityRegistry` (e.g., `registry.HasCapability("qat.compression")`)
2. **Load** appropriate driver/binding if present (Intel QAT SDK, CUDA/ROCm interop)
3. **Fallback** gracefully to software implementation when hardware is absent
4. **Transparent API** — callers use `IHardwareAccelerator` without knowing if hardware or software is used

**Key insight**: The SDK runs on ANY machine. Hardware acceleration is purely opportunistic.

### 2. Security Hardware Contracts (HW-03, HW-04)

- **TPM2**: Use TSS.MSR on Windows, `/dev/tpmrm0` on Linux. Keys never leave TPM. Sealing to PCR values ensures platform integrity.
- **HSM**: PKCS#11 is the universal HSM interface. All major HSMs (Thales, AWS CloudHSM, Azure Dedicated HSM) support it. Key material NEVER exported from HSM — all crypto ops happen inside the device.

### 3. Hypervisor Detection (HW-05)

Detection strategy:
- **VMware ESXi**: Check CPUID leaf 0x40000000 for "VMwareVMware" string
- **Hyper-V**: Check CPUID leaf 0x40000000 for "Microsoft Hv" string, or check registry `HKLM\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters`
- **KVM/QEMU**: Check `/sys/hypervisor/type` on Linux, or CPUID leaf 0x40000000 for "KVMKVMKVM"
- **Xen**: Check `/proc/xen/capabilities` on Linux, or CPUID
- **Bare Metal**: None of the above detected

Optimization hints: Each hypervisor suggests paravirtualized I/O paths (virtio-blk, PVSCSI), clock sources, balloon drivers.

### 4. NUMA-Aware Allocation (HW-06)

**Rationale**: On multi-socket NUMA systems, accessing memory on a remote NUMA node is 2-3x slower than local access. Storage buffers should be allocated on the NUMA node closest to the storage controller.

**Implementation**:
- Discover NUMA topology via Windows `GetNumaHighestNodeNumber` / Linux `libnuma`
- Query storage controller NUMA affinity (PCI device locality)
- Pin buffer allocation to controller-local NUMA node
- Fallback chain: local → adjacent → any node

### 5. NVMe Command Passthrough (HW-07)

**Purpose**: Direct NVMe command submission bypassing the OS filesystem for maximum throughput.

**Platform APIs**:
- **Windows**: `IOCTL_STORAGE_PROTOCOL_COMMAND` with `STORAGE_PROTOCOL_SPECIFIC_NVME` structure
- **Linux**: `/dev/nvmeX` ioctl with `NVME_IOCTL_ADMIN_CMD` and `NVME_IOCTL_IO_CMD`
- **SPDK**: User-space NVMe driver (polling mode, no interrupts) — highest throughput, requires privileged access

**Safety**: Namespace isolation ensures passthrough operations don't corrupt OS filesystem metadata.

### 6. Balloon Driver Cooperation (HW-06)

Balloon drivers (VMware vmballoon, Hyper-V hv_balloon, Xen balloon) allow hypervisor to reclaim guest memory under pressure. DataWarehouse should cooperate:
- Register for memory pressure notifications
- Release configured amount of cache buffers when pressure detected
- Re-acquire memory when pressure subsides

### 7. Integration Pattern

All Phase 35 implementations live in:
- `DataWarehouse.SDK/Hardware/Accelerators/` — QAT, GPU, TPM2, HSM implementations
- `DataWarehouse.SDK/Hardware/Hypervisor/` — hypervisor detection, balloon driver, NUMA awareness
- `DataWarehouse.SDK/Hardware/NVMe/` — NVMe passthrough

All use existing contracts from `IHardwareAcceleration.cs` (ITpmProvider, IHsmProvider, IHardwareAccelerator) and new contracts for hypervisor/NUMA/NVMe.

---

## External Dependencies

| Requirement | NuGet Package | Notes |
|-------------|---------------|-------|
| HW-01 (QAT) | None (P/Invoke to QAT lib) | Intel QAT SDK must be installed on host |
| HW-02 (GPU) | System.Runtime.InteropServices | CUDA/ROCm via P/Invoke or COM |
| HW-03 (TPM2) | Microsoft.TSS | TSS.MSR for TPM 2.0 communication |
| HW-04 (HSM) | Pkcs11Interop | PKCS#11 wrapper for .NET |
| HW-05 (Hypervisor) | None (CPUID intrinsics) | Use `System.Runtime.Intrinsics.X86.X86Base.CpuId` |
| HW-06 (NUMA) | None (P/Invoke to kernel32/libnuma) | Windows API / Linux libnuma |
| HW-07 (NVMe) | None (DeviceIoControl/ioctl) | Platform-specific ioctl wrappers |

**Critical**: All external dependencies are OPTIONAL. If QAT SDK is not installed, QAT accelerator reports `IsAvailable = false` and falls back to software. Zero hard dependencies.

---

## Wave Structure

**Wave 1**: HW-01 (QAT) + HW-02 (GPU) — both are IHardwareAccelerator implementations, parallel
**Wave 2**: HW-03 (TPM2) + HW-04 (HSM) — both are security providers, parallel, no overlap
**Wave 3**: HW-05 (Hypervisor Detection) — standalone, needed for Wave 4
**Wave 4**: HW-06 (Balloon + NUMA) + HW-07 (NVMe Passthrough) — parallel, both depend on HW-05 for environment detection

Total: 4 waves, 7 plans

---

## Risk Mitigation

1. **No hardware available for testing**: All implementations include software fallback. Unit tests verify fallback path.
2. **Platform-specific code**: Use `RuntimeInformation.IsOSPlatform()` guards. NullProvider pattern for unsupported platforms.
3. **Privileged access required**: All operations check permissions and return `IsAvailable = false` if insufficient permissions.
4. **External SDK dependencies**: Document installation requirements. Provide detection at runtime (file existence checks before loading libraries).

---

## Success Criteria

- All 7 requirements (HW-01 through HW-07) implemented
- Zero breaking changes (all additive)
- Zero new hard dependencies (all optional at runtime)
- Software fallback for every hardware integration
- Platform capability registry correctly reports all new capabilities
- All code builds on Windows, Linux, and macOS (with conditional compilation where needed)
