---
phase: 107-chaos-plugin-faults-lifecycle
plan: 01
subsystem: testing
tags: [chaos-engineering, fault-injection, kernel-isolation, message-bus, plugin-lifecycle]

requires:
  - phase: 106-02
    provides: "Soak test results proving baseline stability"
provides:
  - "7 plugin fault injection chaos tests proving Kernel + message bus isolation"
  - "FaultyTestPlugin with 7 configurable fault modes"
  - "HealthyTestPlugin as control with atomic counters and subscription tracking"
affects: [107-02, 108, 109, 110]

tech-stack:
  added: []
  patterns: [chaos-fault-injection, subscriber-exception-isolation, subscription-lifecycle-cleanup]

key-files:
  created:
    - DataWarehouse.Hardening.Tests/Chaos/PluginFault/PluginFaultInjectionTests.cs
    - DataWarehouse.Hardening.Tests/Chaos/PluginFault/FaultyTestPlugin.cs
    - DataWarehouse.Hardening.Tests/Chaos/PluginFault/HealthyTestPlugin.cs
  modified: []

key-decisions:
  - "Used AccessViolationException instead of StackOverflowException for severe fault testing (StackOverflow uncatchable in .NET)"
  - "Direct message bus subscriptions in tests rather than relying on OnKernelServicesInjected for deterministic control"
  - "7 fault modes in FaultyTestPlugin: OOM, UnhandledException, TaskCanceled, Hang, AccessViolation, AggregateFailure, None"

patterns-established:
  - "Chaos test pattern: KernelBuilder.Create() -> register plugins -> subscribe to topics -> arm fault -> publish -> assert healthy continues"
  - "Subscription cleanup pattern: dispose faulty subscription after fault, verify GetSubscriberCount decreases"

requirements-completed: [CHAOS-01]

duration: 12min
completed: 2026-03-07
---

# Phase 107 Plan 01: Plugin Fault Injection Summary

**7 chaos tests proving Kernel isolates fatal plugin exceptions (OOM, AV, AggregateException, TaskCanceled) without crashing -- healthy plugins continue operating**

## Performance

- **Duration:** 12 min
- **Started:** 2026-03-07T06:12:38Z
- **Completed:** 2026-03-07T06:24:33Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- FaultyTestPlugin with 7 configurable fault modes (OOM, AccessViolation, TaskCanceled, Hang, AggregateFailure, UnhandledException, None)
- HealthyTestPlugin as control with atomic operation counter, message counter, and subscription tracking
- 7 passing chaos tests proving complete Kernel/message bus isolation:
  1. OOM fault -- healthy subscribers continue
  2. AccessViolation fault -- no process crash
  3. Subscription cleanup -- no orphaned subscriptions after fault+dispose
  4. 10 repeated fault cycles -- kernel remains stable
  5. Fault during publish -- all healthy subscribers still receive messages
  6. AggregateException -- gracefully handled
  7. TaskCanceledException -- other operations continue

## Task Commits

Each task was committed atomically:

1. **Task 1: Create fault injection test plugins** - `e8fc0126` (feat)
2. **Task 2: Implement plugin fault injection tests** - `a2659c27` (test)

## Files Created/Modified
- `DataWarehouse.Hardening.Tests/Chaos/PluginFault/FaultyTestPlugin.cs` - Configurable fault injection plugin (115 lines, 7 fault modes)
- `DataWarehouse.Hardening.Tests/Chaos/PluginFault/HealthyTestPlugin.cs` - Control plugin with atomic counters (92 lines)
- `DataWarehouse.Hardening.Tests/Chaos/PluginFault/PluginFaultInjectionTests.cs` - 7 chaos test cases (422 lines)

## Decisions Made
- Used AccessViolationException instead of StackOverflowException for severe fault testing -- StackOverflowException cannot be caught in .NET, making it untestable in-process
- Direct message bus subscriptions in tests rather than relying on plugin's OnKernelServicesInjected callback -- provides deterministic test control over when faults fire
- Tests verify message bus exception isolation (try/catch in PublishAndWaitAsync) rather than AssemblyLoadContext unloading -- the message bus is the actual isolation boundary for subscriber faults

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed OOM test using direct subscriptions instead of implicit plugin registration**
- **Found during:** Task 2 (test execution)
- **Issue:** First version of KernelIsolates_OomFault test relied on HealthyTestPlugin's OnKernelServicesInjected to subscribe, but the FaultyTestPlugin was not subscribed to the same topic, causing assertion failure
- **Fix:** Changed to explicit Subscribe() calls on both sides for deterministic fault injection
- **Files modified:** PluginFaultInjectionTests.cs
- **Verification:** All 7 tests pass
- **Committed in:** a2659c27

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Minor test design adjustment. No scope creep.

## Issues Encountered
None beyond the auto-fixed deviation above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plugin fault injection chaos tests complete, ready for 107-02 (plugin lifecycle chaos testing)
- Chaos test infrastructure (FaultyTestPlugin, HealthyTestPlugin) reusable for subsequent chaos phases

---
*Phase: 107-chaos-plugin-faults-lifecycle*
*Completed: 2026-03-07*

## Self-Check: PASSED
- All 4 files verified present on disk
- Both task commits verified in git log (e8fc0126, a2659c27)
- All 7 tests pass: `dotnet test --filter "FullyQualifiedName~PluginFault"` = 7/7 passed
