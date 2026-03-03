---
phase: 84-deployment-topology-cli-modes
plan: 01
subsystem: hosting
tags: [deployment-topology, cli, install, vde, dw-kernel]

requires:
  - phase: 83-integration-testing
    provides: "Verified policy/cascade/VDE format foundation"
provides:
  - "DeploymentTopology enum (DwPlusVde, DwOnly, VdeOnly)"
  - "DeploymentTopologyDescriptor helper methods"
  - "InstallConfiguration topology properties (Topology, RemoteVdeUrl, VdeListenPort)"
  - "CLI --topology, --remote-vde, --vde-port options on install command"
affects: [84-02, 84-03, 84-04, 84-05, 84-06]

tech-stack:
  added: []
  patterns: ["DeploymentTopology enum for component deployment selection", "Topology descriptor static helpers"]

key-files:
  created:
    - DataWarehouse.SDK/Hosting/DeploymentTopology.cs
  modified:
    - DataWarehouse.SDK/Hosting/InstallConfiguration.cs
    - DataWarehouse.CLI/Commands/InstallCommand.cs
    - DataWarehouse.CLI/Program.cs

key-decisions:
  - "DwPlusVde is default (value 0) for backward compatibility"
  - "VDE-only topology skips admin creation automatically"
  - "DW-only without --remote-vde emits warning, not error"

patterns-established:
  - "DeploymentTopology enum: 3-value topology model for all deployment decisions"
  - "DeploymentTopologyDescriptor: static helper pattern for topology queries"

duration: 4min
completed: 2026-02-23
---

# Phase 84 Plan 01: Deployment Topology Model & CLI Install Topology Flag Summary

**DeploymentTopology enum with DwPlusVde/DwOnly/VdeOnly values, descriptor helpers, and CLI --topology flag on install command**

## Performance

- **Duration:** 4 min
- **Started:** 2026-02-23T18:36:43Z
- **Completed:** 2026-02-23T18:40:46Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- DeploymentTopology enum with 3 values (DwPlusVde=0, DwOnly=1, VdeOnly=2) in SDK
- DeploymentTopologyDescriptor static class with RequiresDwKernel, RequiresVdeEngine, RequiresRemoteVdeConnection, AcceptsRemoteDwConnections, GetDescription helpers
- InstallConfiguration extended with Topology, RemoteVdeUrl, VdeListenPort properties
- CLI install command accepts --topology (dw-only|vde-only|dw+vde), --remote-vde, --vde-port options
- VDE-only topology skips DW-specific init (admin creation disabled, listen port stored)
- DW-only without remote VDE URL emits user-facing warning

## Task Commits

Each task was committed atomically:

1. **Task 1: DeploymentTopology enum and InstallConfiguration update** - `401be055` (feat)
2. **Task 2: Wire --topology into CLI install command** - `163a236c` (feat)

## Files Created/Modified
- `DataWarehouse.SDK/Hosting/DeploymentTopology.cs` - DeploymentTopology enum + DeploymentTopologyDescriptor static helpers
- `DataWarehouse.SDK/Hosting/InstallConfiguration.cs` - Added Topology, RemoteVdeUrl, VdeListenPort properties
- `DataWarehouse.CLI/Commands/InstallCommand.cs` - Topology parsing, validation, VDE-only skip logic
- `DataWarehouse.CLI/Program.cs` - --topology, --remote-vde, --vde-port options wired to install command

## Decisions Made
- DwPlusVde is default (value 0) for backward compatibility with existing installations
- VDE-only topology automatically skips admin creation (no DW kernel to admin)
- DW-only without --remote-vde emits warning (not error) to allow later configuration

## Deviations from Plan
None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- DeploymentTopology enum ready for use in topology-aware host startup (Plan 02+)
- CLI --topology flag operational for all three deployment modes
- Pre-existing CS0618 warnings in DataWarehouse.Shared (obsolete FindLocalLiveInstance) unrelated to this plan

---
*Phase: 84-deployment-topology-cli-modes*
*Completed: 2026-02-23*
