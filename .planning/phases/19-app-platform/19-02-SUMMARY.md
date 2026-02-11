---
phase: 19-app-platform
plan: 02
subsystem: Application Platform Services
tags: [access-policy, rbac, abac, context-router, service-routing, token-validation, message-bus]
dependency_graph:
  requires:
    - phase: 19-01
      provides: AppPlatformPlugin, AppRegistrationService, ServiceTokenService, PlatformTopics
  provides:
    - AppAccessPolicy model with RBAC/ABAC/MAC/DAC/PBAC support
    - AppRequestContext with round-trip payload serialization
    - AppAccessPolicyStrategy for per-app policy binding via message bus
    - AppContextRouter for authenticated service request routing
    - 6 service routing handlers in AppPlatformPlugin
    - 4 policy management handlers in AppPlatformPlugin
  affects: [UltimateAccessControl via accesscontrol.policy.* topics, downstream plugins via enriched messages]
tech_stack:
  added: []
  patterns: [per-app policy isolation via message bus, token-validated request routing, AppRequestContext enrichment, scope-based authorization]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.AppPlatform/Models/AppAccessPolicy.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/Models/AppRequestContext.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/Strategies/AppAccessPolicyStrategy.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/Services/AppContextRouter.cs
  modified:
    - Plugins/DataWarehouse.Plugins.AppPlatform/Models/PlatformTopics.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/AppPlatformPlugin.cs
key_decisions:
  - "AppAccessPolicy supports 5 access control models (RBAC/ABAC/MAC/DAC/PBAC) with tenant isolation defaults"
  - "AppRequestContext uses ToDictionary/FromPayload for round-trip serialization into PluginMessage.Payload"
  - "AppContextRouter validates token, scope, and app status in a 3-step pipeline before forwarding"
  - "Policy topics use platform.policy.* namespace separate from accesscontrol.policy.* downstream topics"
patterns_established:
  - "Token-validated routing: every service request goes through RouteServiceRequestAsync with 3-step auth pipeline"
  - "Message enrichment: forwarded messages always include AppId, AppContext (dictionary), and ServiceTokenId"
  - "Scope-per-service: each route method requires a specific scope string matching the service name"
metrics:
  duration: 5 min
  completed: 2026-02-11
---

# Phase 19 Plan 02: Access Policy Isolation and Service Routing Summary

**Per-app RBAC/ABAC policy binding via message bus, AppContextRouter with token-validated scope-checked request routing, and AppRequestContext enrichment for downstream plugins.**

## Performance

- **Duration:** 5 min
- **Started:** 2026-02-11T09:34:51Z
- **Completed:** 2026-02-11T09:40:22Z
- **Tasks:** 2
- **Files modified:** 6

## Accomplishments
- Per-app access control policy model supporting 5 access control models (RBAC, ABAC, MAC, DAC, PBAC) with configurable tenant isolation
- AppRequestContext with round-trip serialization (ToDictionary/FromPayload) for standardized context propagation
- AppAccessPolicyStrategy that binds/unbinds/updates/evaluates policies through UltimateAccessControl via message bus
- AppContextRouter with 3-step validation pipeline (token, scope, app status) and message enrichment
- All 6 service routes (storage, accesscontrol, intelligence, observability, replication, compliance) wired in AppPlatformPlugin
- All 4 policy management operations (bind, unbind, get, evaluate) wired in AppPlatformPlugin

## Task Commits

Each task was committed atomically:

1. **Task 1: Create AppAccessPolicy model, AppRequestContext, and AppAccessPolicyStrategy** - `f211ce6` (feat)
2. **Task 2: Implement AppContextRouter and wire into AppPlatformPlugin** - `3cb5b7c` (feat)

## Files Created/Modified

- `Models/AppAccessPolicy.cs` - Sealed record with RBAC roles, ABAC attributes, tenant isolation config; enums for AccessControlModel and AttributeOperator
- `Models/AppRequestContext.cs` - Sealed record with AppId, TokenId, AllowedScopes, timestamps; ToDictionary/FromPayload for message payload embedding
- `Strategies/AppAccessPolicyStrategy.cs` - Internal sealed class with ConcurrentDictionary, BindPolicyAsync/UnbindPolicyAsync/EvaluateAccessAsync via message bus
- `Services/AppContextRouter.cs` - Internal sealed class with RouteServiceRequestAsync (token validation, scope check, app status, message enrichment) and 6 service-specific route methods
- `Models/PlatformTopics.cs` - Extended with PolicyBind, PolicyUnbind, PolicyGet, PolicyEvaluate constants
- `AppPlatformPlugin.cs` - Added AppAccessPolicyStrategy and AppContextRouter fields, 6 service routing subscriptions, 4 policy management subscriptions, ExtractRoles/ExtractAttributes helpers

## Decisions Made

- AppAccessPolicy supports all 5 access control models from the plan (RBAC/ABAC/MAC/DAC/PBAC) with tenant isolation enforced by default
- AppRequestContext.ToDictionary serializes DateTime as ISO 8601 round-trip format for reliable deserialization
- AppContextRouter uses a single RouteServiceRequestAsync with requiredScope parameter; service-specific methods are thin wrappers
- Policy topics use `platform.policy.*` prefix (platform-facing), distinct from `accesscontrol.policy.*` (downstream UltimateAccessControl-facing)
- ExtractRoles/ExtractAttributes handle both typed objects and JsonElement deserialization for flexibility

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed XML doc cref to AppContextRouter**
- **Found during:** Task 1 (AppRequestContext.cs creation)
- **Issue:** XML comment referenced `AppContextRouter` via `<see cref="AppContextRouter"/>` but that class did not exist yet (created in Task 2), causing CS1574 build error
- **Fix:** Changed to plain text reference `AppContextRouter` without cref
- **Files modified:** Models/AppRequestContext.cs
- **Verification:** Build passes with zero errors
- **Committed in:** f211ce6 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** Minor documentation adjustment. No scope creep.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Per-app access policies and service routing complete
- Ready for 19-03 (per-app AI workflow configuration and observability isolation)
- AppContextRouter pattern can be extended for additional services
- Downstream plugins will receive AppId, AppContext, and ServiceTokenId in every forwarded message

## Self-Check: PASSED

All 6 files verified present on disk. Both commit hashes (f211ce6, 3cb5b7c) verified in git log.

---
*Phase: 19-app-platform*
*Completed: 2026-02-11*
