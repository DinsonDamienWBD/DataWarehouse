---
phase: 91.5-vde-v2.1-format-completion
plan: 46
subsystem: vde
tags: [spdk, nvme, p/invoke, dma, preamble, bare-metal, vfio-pci]

requires:
  - phase: 91.5-87-45
    provides: "Preamble header struct and detection infrastructure"
provides:
  - "SpdkBlockDevice implementing IBlockDevice for preamble bare-metal NVMe boot"
  - "SpdkDmaAllocator for pool-based DMA-safe buffer management"
  - "SpdkNativeBindings P/Invoke declarations for libspdk_nvme preamble driver"
affects: [87-47, 87-48, 87-59]

tech-stack:
  added: [libspdk_nvme, LibraryImport, GCHandle-pinned-completion]
  patterns: [unmanaged-callback-completion, dma-buffer-pool, gchandle-pin-for-async]

key-files:
  created:
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/SpdkNativeBindings.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/SpdkDmaAllocator.cs
    - DataWarehouse.SDK/VirtualDiskEngine/Preamble/SpdkBlockDevice.cs

key-decisions:
  - "Used GCHandle.Alloc with Pinned type for completion flags to safely bridge unmanaged callbacks with async/await"
  - "Separate libspdk_nvme bindings from existing IO/Spdk/ to keep preamble boot path isolated from hosted SPDK"
  - "Pool growth on exhaustion rather than blocking to ensure I/O path never waits on allocation"

patterns-established:
  - "GCHandle-pinned completion flag pattern for async P/Invoke polling without unsafe async methods"
  - "ConcurrentStack-based DMA buffer pooling with transparent growth"

duration: 7min
completed: 2026-03-02
---

# Phase 91.5 Plan 46: Preamble SPDK Block Device Summary

**SPDK-backed IBlockDevice with P/Invoke bindings and DMA buffer pool for preamble bare-metal NVMe boot via vfio-pci**

## Performance

- **Duration:** 7 min
- **Started:** 2026-03-02T14:48:24Z
- **Completed:** 2026-03-02T14:55:44Z
- **Tasks:** 1
- **Files created:** 3

## Accomplishments
- SpdkNativeBindings with full P/Invoke surface for libspdk_nvme: env init, probe/attach, namespace, queue pair, read/write/flush, DMA alloc/free, and completion polling
- SpdkDmaAllocator with pre-warmed ConcurrentStack pool, lock-free rent/return, transparent growth, and proper Dispose cleanup
- SpdkBlockDevice implementing IBlockDevice with GCHandle-pinned async completion polling, periodic yield to avoid thread starvation, and proper resource lifecycle

## Task Commits

Each task was committed atomically:

1. **Task 1: SPDK native bindings, DMA allocator, and SpdkBlockDevice** - `ab28e216` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/SpdkNativeBindings.cs` - P/Invoke declarations for libspdk_nvme preamble driver pack with SuppressUnmanagedCodeSecurity, managed structs, and runtime detection
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/SpdkDmaAllocator.cs` - Pool-based DMA-safe memory allocator with ConcurrentStack, growth on exhaustion, and diagnostics
- `DataWarehouse.SDK/VirtualDiskEngine/Preamble/SpdkBlockDevice.cs` - IBlockDevice implementation with SPDK env init, probe/attach, async read/write/flush via GCHandle-pinned completion flags

## Decisions Made
- Used GCHandle.Alloc(Pinned) for completion flags instead of `ref int` or `int*` parameters on async methods (C# prohibits ref/pointer params on async methods)
- Kept preamble SPDK bindings in separate Preamble namespace from existing IO/Spdk bindings because preamble links against stripped libspdk_nvme from preamble payload, not the full SPDK installation
- DMA allocator grows transparently when pool exhausted rather than blocking or throwing, ensuring the I/O hot path never stalls

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed async method with ref/pointer parameters**
- **Found during:** Task 1 (SpdkBlockDevice implementation)
- **Issue:** C# does not allow `ref int` or `int*` parameters on async methods (CS1988, CS4005)
- **Fix:** Used GCHandle.Alloc(completionFlag, GCHandleType.Pinned) to pin int[] arrays, passing AddrOfPinnedObject() to unmanaged callbacks and polling via Volatile.Read on the array
- **Files modified:** SpdkBlockDevice.cs
- **Verification:** Build compiles without errors in Preamble files
- **Committed in:** ab28e216

**2. [Rule 1 - Bug] Fixed `fixed` on already-fixed buffer field**
- **Found during:** Task 1 (SpdkBlockDevice InitializeAsync)
- **Issue:** CS0213 - cannot use `fixed` statement on a `fixed` buffer field (already pinned in struct)
- **Fix:** Accessed fixed buffer pointer directly via `envOpts.Name` without additional `fixed` statement
- **Files modified:** SpdkBlockDevice.cs
- **Verification:** Build compiles without errors
- **Committed in:** ab28e216

---

**Total deviations:** 2 auto-fixed (2 bugs)
**Impact on plan:** Both fixes were necessary C# language constraint workarounds. No scope creep.

## Issues Encountered
None beyond the auto-fixed compilation issues above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- SPDK preamble block device ready for integration with preamble boot pipeline
- Plan 87-47 (stripped kernel build spec) and 87-48 can proceed
- Plan 87-59 (general-purpose SPDK device) is independent and can reference the same pattern

---
*Phase: 91.5-vde-v2.1-format-completion*
*Completed: 2026-03-02*

## Self-Check: PASSED
