---
phase: 19-app-platform
plan: 01
subsystem: Application Platform Services
tags: [app-registration, service-tokens, platform-plugin, message-bus, tenant-provisioning]
dependency_graph:
  requires: [DataWarehouse.SDK]
  provides: [AppPlatformPlugin, AppRegistrationService, ServiceTokenService, PlatformTopics]
  affects: [UltimateAccessControl via message bus]
tech_stack:
  added: []
  patterns: [IntelligenceAwarePluginBase, ConcurrentDictionary, SHA256 hash-based tokens, message bus tenant provisioning]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.AppPlatform/DataWarehouse.Plugins.AppPlatform.csproj
    - Plugins/DataWarehouse.Plugins.AppPlatform/Models/AppRegistration.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/Models/ServiceToken.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/Models/PlatformTopics.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/Services/AppRegistrationService.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/Services/ServiceTokenService.cs
    - Plugins/DataWarehouse.Plugins.AppPlatform/AppPlatformPlugin.cs
  modified:
    - DataWarehouse.slnx
decisions:
  - Used SHA256.HashData (not HMAC) for token hashing per plan spec
  - AppPlatformPlugin extends IntelligenceAwarePluginBase for T90 AI integration
  - All platform topics use "platform." prefix for namespace isolation
  - Validation cache uses 30-second TTL for performance
metrics:
  duration: 4 min
  completed: 2026-02-11
---

# Phase 19 Plan 01: AppPlatform Plugin Foundation Summary

AppPlatform plugin with app registration CRUD, SHA256 hash-based service tokens, and tenant provisioning via message bus to UltimateAccessControl.

## What Was Built

### Task 1: Plugin Project, Models, and Topic Constants (c8a84d4)

Created the `DataWarehouse.Plugins.AppPlatform` project with SDK-only dependency, three model files, and solution integration:

- **AppRegistration.cs**: `sealed record AppRegistration` with AppId, AppName, OwnerUserId, CallbackUrls, Status, timestamps, ServiceConfig, and Metadata. `enum AppStatus` (Pending/Active/Suspended/Deactivated/Deleted). `sealed record AppServiceConfig` with MaxUsers (100), MaxResources (10000), MaxStorageBytes (1 GiB), EnabledServices (storage/accesscontrol/intelligence/observability).
- **ServiceToken.cs**: `sealed record ServiceToken` with TokenId, AppId, TokenHash (SHA-256), timestamps, AllowedScopes, revocation fields. `sealed record TokenValidationResult` with IsValid, AppId, TokenId, AllowedScopes, FailureReason.
- **PlatformTopics.cs**: `internal static class` with 15 const string fields covering app lifecycle (register/deregister/update/get/list), token management (create/rotate/revoke/validate), and service routing (storage/accesscontrol/intelligence/observability/replication/compliance).

### Task 2: Services and Plugin (8c8c205)

Implemented the three core files that make the plugin operational:

- **AppRegistrationService.cs**: `internal sealed class` with ConcurrentDictionary storage. RegisterAppAsync generates GUID AppId, stores registration, and sends `accesscontrol.tenant.register` via message bus with TenantId, TenantName, MaxUsers, MaxResources, MaxStorageBytes, IsolationLevel=Strict. DeregisterAppAsync marks Deleted and sends `accesscontrol.tenant.deregister`. GetAppAsync, ListAppsAsync (active only), UpdateAppAsync (mutable fields), SuspendAppAsync, ReactivateAppAsync all thread-safe.
- **ServiceTokenService.cs**: `internal sealed class` with ConcurrentDictionary storage and validation cache (30s TTL). CreateTokenAsync generates 32 random bytes via RandomNumberGenerator, Base64 encodes as rawKey, computes SHA256.HashData hash, stores only hash. ValidateTokenAsync checks cache first, then matches hash against stored tokens (not revoked, not expired). RotateTokenAsync validates old, revokes old (reason: "Rotated"), creates new. RevokeTokenAsync and RevokeAllTokensForAppAsync with cache invalidation.
- **AppPlatformPlugin.cs**: `public sealed class` extending IntelligenceAwarePluginBase + IDisposable. Overrides OnStartWithIntelligenceAsync and OnStartWithoutIntelligenceAsync (both call InitializeServices). Subscribes to 9 platform topics via response-capable overload. Each handler extracts payload fields, delegates to service, returns MessageResponse.Ok or MessageResponse.Error. Full Dispose pattern with subscription cleanup.

## Deviations from Plan

None - plan executed exactly as written.

## Verification Results

1. `dotnet build` compiles with zero errors (44 pre-existing SDK warnings only)
2. Plugin references ONLY `DataWarehouse.SDK.csproj` - confirmed single ProjectReference
3. AppPlatformPlugin extends IntelligenceAwarePluginBase and implements IDisposable
4. All model types are sealed records with required properties
5. ServiceTokenService.ComputeHash uses SHA256.HashData from System.Security.Cryptography
6. AppRegistrationService.RegisterAppAsync sends message to `accesscontrol.tenant.register` topic
7. All classes have XML documentation comments
8. Plugin added to DataWarehouse.slnx alphabetically (after ApacheSuperset, before AuditLogging)

## Self-Check: PASSED

All 7 created files exist. Both commit hashes (c8a84d4, 8c8c205) verified in git log.
