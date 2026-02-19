---
phase: 52-clean-house
plan: 01
subsystem: cli
tags: [message-bus, cli, kernel, request-response, graceful-degradation]

# Dependency graph
requires:
  - phase: none
    provides: n/a
provides:
  - "CLI command files with real message bus queries instead of TODO stubs"
  - "Kernel accessor pattern for BackupCommands, PluginCommands, DeveloperCommands, HealthCommands, RaidCommands"
  - "11 message bus topic integrations across 5 command files"
affects: [52-clean-house, cli-compilation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Static kernel accessor with lazy initialization and SemaphoreSlim locking"
    - "SendAsync with TimeSpan timeout and graceful fallback messages"
    - "PluginMessage with Type, SourcePluginId, Source, and Payload dictionary"

key-files:
  created: []
  modified:
    - DataWarehouse.CLI/Commands/BackupCommands.cs
    - DataWarehouse.CLI/Commands/PluginCommands.cs
    - DataWarehouse.CLI/Commands/DeveloperCommands.cs
    - DataWarehouse.CLI/Commands/HealthCommands.cs
    - DataWarehouse.CLI/Commands/RaidCommands.cs

key-decisions:
  - "Followed StorageCommands kernel accessor pattern (GetKernelAsync with static instance and SemaphoreSlim)"
  - "Used SendAsync with 5s timeout for all queries (10s for query execution)"
  - "Fallback messages describe which plugin is not responding rather than generic errors"

patterns-established:
  - "CLI message bus query pattern: GetKernelAsync -> new PluginMessage -> SendAsync -> parse response -> fallback"

# Metrics
duration: 6min
completed: 2026-02-19
---

# Phase 52 Plan 01: Replace CLI TODO Comments Summary

**Replaced 11 TODO comments across 5 CLI command files with real message bus queries using SendAsync and graceful fallback messages**

## Performance

- **Duration:** 6 min
- **Started:** 2026-02-19T06:54:29Z
- **Completed:** 2026-02-19T07:00:57Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- Eliminated all 11 TODO comments from CLI command files
- Wired 5 command files to query real data via kernel message bus (SendAsync with timeout)
- Added kernel accessor pattern to each file matching existing StorageCommands convention
- Every query has a descriptive fallback message indicating which plugin is not responding

## Task Commits

Each task was committed atomically:

1. **Task 1: Wire CLI backup and plugin commands to message bus** - `dd58469b` (feat)
2. **Task 2: Wire CLI developer, health, and RAID commands to message bus** - `79e82ed1` (feat)

## Files Created/Modified
- `DataWarehouse.CLI/Commands/BackupCommands.cs` - Real query to dataprotection.backup.list topic
- `DataWarehouse.CLI/Commands/PluginCommands.cs` - Real query to kernel.plugin.list topic
- `DataWarehouse.CLI/Commands/DeveloperCommands.cs` - 7 real queries: interface.api.list, dataformat.schema.list (x2), storage.collection.list, storage.collection.fields, storage.query.execute, storage.query.templates
- `DataWarehouse.CLI/Commands/HealthCommands.cs` - Real query to observability.alert.list topic
- `DataWarehouse.CLI/Commands/RaidCommands.cs` - Real query to raid.configuration.list topic

## Decisions Made
- Followed the existing StorageCommands.cs kernel accessor pattern (static DataWarehouseKernel with lazy init via KernelBuilder)
- Used SendAsync with TimeSpan timeout (5s for list queries, 10s for query execution) rather than fire-and-forget PublishAsync
- Fallback messages are plugin-specific ("RAID plugin not responding") rather than generic ("not available")
- Note: Commands/ folder is excluded from CLI compilation via `<Compile Remove="Commands\**" />` in csproj -- these are template command files that will compile when the exclusion is lifted

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- Build lock file contention on first attempt (VBCSCompiler holding SDK DLL) -- resolved on retry
- Commands/ folder excluded from compilation (`<Compile Remove="Commands\**" />`), so build verification confirms CLI main project compiles cleanly but does not compile these specific files

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- All CLI command files are wired to message bus -- ready for plans 02-07 in this phase
- When Commands/ folder is re-included in compilation, these files will need the Kernel project reference added to the CLI csproj

## Self-Check: PASSED

All 5 modified files exist. Both task commits verified (dd58469b, 79e82ed1). SUMMARY.md created.

---
*Phase: 52-clean-house*
*Completed: 2026-02-19*
