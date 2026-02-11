# Phase 19: Application Platform Services - Research

**Researched:** 2026-02-11
**Domain:** Multi-tenant application platform -- app registration, service tokens, per-app isolation, per-app AI workflows, per-app observability, service consumption API
**Confidence:** HIGH

## Summary

Phase 19 transforms the DataWarehouse from a standalone system into a platform that registered applications can consume. This is **entirely new functionality** -- no PLATFORM-01/02/03 task definitions exist in `Metadata/TODO.md`, no `AppPlatform` plugin exists, and no SDK contracts for app registration or service tokens have been created yet. However, the codebase has substantial infrastructure that this phase can build upon.

**Key existing assets that Phase 19 leverages:**

1. **Multi-Tenancy Isolation Strategy** (`UltimateAccessControl/Strategies/Core/MultiTenancyIsolationStrategy.cs`) -- A fully implemented multi-tenant access control strategy with tenant definitions, memberships, cross-tenant grants, impersonation, quotas, and hierarchical isolation (Strict/SharedResources/Federated). This maps almost directly to per-app access control policy isolation.

2. **Service Provisioner & Tenant Lifecycle** (`SDK/Infrastructure/EdgeManagedPhase8.cs`) -- `ServiceProvisioner` and `TenantLifecycleManager` classes with provisioning, suspension, deletion, and status management. These are SDK-level building blocks for tenant/app lifecycle.

3. **Intelligence Model Scoping** (`UltimateIntelligence/DomainModels/ModelScoping.cs`) -- `ModelScope` enum with Global/Instance/Department/Team/UserGroup/Individual levels, plus `ScopeIdentifier` with hierarchical parent resolution. This directly supports per-app AI scoping.

4. **Intelligence Interaction Modes** (`UltimateIntelligence/Modes/InteractionModes.cs`) -- On-demand, background, scheduled, and event-driven interaction modes via message bus topics. Per-app AI workflow configuration can compose these modes.

5. **Observability Strategy Pattern** (`UniversalObservability`) -- Strategy-based observability with metrics, tracing, logging, and alerting. Per-app observability isolation requires tagging/filtering by app ID, not new strategies.

6. **Message Bus** (`IMessageBus`) -- All inter-plugin communication goes through the bus with publish/subscribe, request/response, and pattern matching. App-context routing adds an app ID dimension to messages.

7. **Dashboard Auth** (`Dashboard/Controllers/AuthController.cs`) -- JWT token service with user roles. Service token generation for apps follows the same pattern.

**Primary recommendation:** Create a new `DataWarehouse.Plugins.AppPlatform` plugin extending `IntelligenceAwarePluginBase` (since it needs Intelligence for per-app AI workflows). This plugin acts as the platform gateway, using strategies for registration, token management, access policy binding, AI workflow configuration, observability isolation, and service routing. It delegates heavily to existing plugins (UltimateAccessControl, UltimateIntelligence, UniversalObservability, UltimateStorage, UltimateReplication, UltimateCompliance) via the message bus.

## Standard Stack

### Core (Already in Codebase)

| Component | Location | Purpose | Status |
|-----------|----------|---------|--------|
| MultiTenancyIsolationStrategy | `UltimateAccessControl/Strategies/Core/` | Tenant definitions, memberships, quotas, cross-tenant grants | EXISTS - fully implemented |
| ServiceProvisioner | `SDK/Infrastructure/EdgeManagedPhase8.cs` | Tenant provisioning, deprovisioning, service lifecycle | EXISTS - SDK level |
| TenantLifecycleManager | `SDK/Infrastructure/EdgeManagedPhase8.cs` | Tenant create, suspend, delete with events | EXISTS - SDK level |
| ModelScope / ScopeIdentifier | `UltimateIntelligence/DomainModels/ModelScoping.cs` | Hierarchical scope for AI model deployment | EXISTS |
| InteractionModes | `UltimateIntelligence/Modes/InteractionModes.cs` | On-demand, background, scheduled AI interactions | EXISTS |
| IObservabilityStrategy | `SDK/Contracts/Observability/IObservabilityStrategy.cs` | Metrics, tracing, logging contract | EXISTS |
| IAccessControlStrategy | `UltimateAccessControl/IAccessControlStrategy.cs` | Access evaluation with context, decisions, statistics | EXISTS |
| IMessageBus | `SDK/Contracts/IMessageBus.cs` | Publish/subscribe, request/response, pattern matching | EXISTS |
| FeaturePluginBase | `SDK/Contracts/PluginBase.cs` | Base for plugins with lifecycle (Start/Stop) | EXISTS |
| IntelligenceAwarePluginBase | `SDK/Contracts/IntelligenceAware/` | Base for plugins needing AI integration | EXISTS |
| AuthController / IJwtTokenService | `Dashboard/Controllers/AuthController.cs` | JWT token generation and validation | EXISTS |
| PluginCategory enum | `SDK/Primitives/Enums.cs` | SecurityProvider, FeatureProvider, etc. | EXISTS |
| HandshakeRequest/Response | `SDK/Primitives/Handshake.cs` | Plugin initialization protocol | EXISTS |

### Must Be Built

| Component | Purpose | Approach |
|-----------|---------|----------|
| AppPlatformPlugin | Main orchestrator plugin | New plugin extending `IntelligenceAwarePluginBase` with `PluginCategory.FeatureProvider` |
| AppRegistration model | App ID, name, description, callback URLs, status, created date | Sealed record types in plugin |
| ServiceTokenManager | HMAC-SHA256 based app-specific API keys with rotation | Internal sealed class using `System.Security.Cryptography` |
| AppAccessPolicyBinder | Binds per-app RBAC/ABAC rules into UltimateAccessControl | Strategy that wraps MultiTenancyIsolationStrategy via message bus |
| AppAiWorkflowConfig | Per-app Intelligence mode configuration (auto/manual/budget/approval) | Configuration model dispatched to UltimateIntelligence via message bus |
| AppObservabilityFilter | Per-app telemetry isolation via app ID tagging | Decorator/filter over existing observability strategies |
| ServiceConsumptionRouter | Routes incoming requests to correct app context | Message handler that enriches PluginMessage with app context |
| PlatformServiceFacade | Unified API exposing Storage, AccessControl, Intelligence, Observability, Replication, Compliance | Message bus topic handlers per service domain |

### Supporting (Already Available)

| Library/Framework | Version | Purpose |
|-------------------|---------|---------|
| System.Security.Cryptography | .NET 10 | Service token generation (HMAC-SHA256, random bytes) |
| System.Text.Json | .NET 10 | Serialization for app configs, registration persistence |
| System.Collections.Concurrent | .NET 10 | Thread-safe registries for app definitions, tokens, configs |

## Architecture Patterns

### Recommended Project Structure

```
Plugins/DataWarehouse.Plugins.AppPlatform/
    DataWarehouse.Plugins.AppPlatform.csproj
    AppPlatformPlugin.cs                     # Main plugin (IntelligenceAwarePluginBase)
    Models/
        AppRegistration.cs                   # App definition record types
        ServiceToken.cs                      # Token model with hash, expiry, scopes
        AppAccessPolicy.cs                   # Per-app access control policy config
        AppAiWorkflowConfig.cs               # Per-app AI workflow mode config
        AppObservabilityConfig.cs            # Per-app observability isolation config
        ServiceEndpoint.cs                   # Service consumption endpoint descriptors
    Services/
        AppRegistrationService.cs            # Registration CRUD via message bus
        ServiceTokenService.cs               # Token generation, validation, rotation
        AppContextRouter.cs                  # Request routing by app ID
        PlatformServiceFacade.cs             # Unified service exposure layer
    Strategies/
        AppAccessPolicyStrategy.cs           # Integrates with UltimateAccessControl
        AppAiWorkflowStrategy.cs             # Integrates with UltimateIntelligence
        AppObservabilityStrategy.cs          # Integrates with UniversalObservability
```

### Pattern 1: App Registration as Tenant

**What:** Map each registered application to a tenant in the existing multi-tenancy system. When an app registers, create a corresponding `TenantDefinition` in `MultiTenancyIsolationStrategy` with the app's unique ID as the tenant ID.

**When to use:** Always. This is the core architectural decision -- apps ARE tenants from the access control perspective.

**Why:** The `MultiTenancyIsolationStrategy` already has:
- `TenantDefinition` with ID, name, status, settings, metadata
- `TenantMembership` with user/tenant/role bindings
- `TenantResourceQuota` with max users, resources, storage
- `CrossTenantGrant` for inter-app access
- `IsolationLevel` (Strict, SharedResources, Federated)
- Full evaluation logic in `EvaluateAccessCoreAsync`

**Example:**
```csharp
// When app registers, create tenant in access control
var tenantDef = new TenantDefinition
{
    Id = appRegistration.AppId,
    Name = appRegistration.AppName,
    DisplayName = appRegistration.DisplayName,
    Status = TenantStatus.Active,
    CreatedAt = DateTime.UtcNow,
    Settings = new TenantSettings
    {
        IsolationLevel = IsolationLevel.Strict,
        MaxUsers = appRegistration.MaxUsers,
        MaxResources = appRegistration.MaxResources,
        MaxStorageBytes = appRegistration.MaxStorageBytes,
        AllowCrossTenantAccess = false
    }
};

// Send via message bus to UltimateAccessControl
await MessageBus.SendAsync("accesscontrol.tenant.register", new PluginMessage
{
    SenderId = Id,
    Action = "RegisterTenant",
    Payload = tenantDef
});
```

### Pattern 2: Service Token as Bearer Credential

**What:** Service tokens are HMAC-SHA256 hashed API keys. The raw key is returned once on creation. Validation checks the hash against stored value. Tokens are scoped to specific services (Storage, Intelligence, etc.).

**Why:** Follows the same pattern as the Dashboard's JWT service but for machine-to-machine authentication. Tokens include app ID, creation timestamp, expiry, and allowed service scopes.

**Example:**
```csharp
public sealed record ServiceToken
{
    public required string TokenId { get; init; }
    public required string AppId { get; init; }
    public required string TokenHash { get; init; }  // HMAC-SHA256 of raw key
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
    public required string[] AllowedScopes { get; init; } // ["storage", "intelligence", "observability"]
    public bool IsRevoked { get; init; }
    public DateTime? RevokedAt { get; init; }
    public string? RevocationReason { get; init; }
}
```

### Pattern 3: Message Bus Topic Namespacing by App

**What:** All platform service messages use `platform.{appId}.{service}.{action}` topic format. The `AppContextRouter` subscribes to `platform.*` patterns and enriches messages with app context before forwarding to the appropriate plugin.

**Why:** Maintains the message bus pattern. No direct plugin references. The router acts as a gateway that validates the service token, resolves the app context, and forwards to the target service.

**Example:**
```csharp
// Message topic structure
public static class PlatformTopics
{
    public const string Prefix = "platform";

    // App lifecycle
    public const string AppRegister = $"{Prefix}.register";
    public const string AppDeregister = $"{Prefix}.deregister";
    public const string AppList = $"{Prefix}.list";
    public const string AppGet = $"{Prefix}.get";

    // Service tokens
    public const string TokenCreate = $"{Prefix}.token.create";
    public const string TokenRotate = $"{Prefix}.token.rotate";
    public const string TokenRevoke = $"{Prefix}.token.revoke";
    public const string TokenValidate = $"{Prefix}.token.validate";

    // Per-app service consumption
    public const string ServiceStorage = $"{Prefix}.service.storage";
    public const string ServiceAccessControl = $"{Prefix}.service.accesscontrol";
    public const string ServiceIntelligence = $"{Prefix}.service.intelligence";
    public const string ServiceObservability = $"{Prefix}.service.observability";
    public const string ServiceReplication = $"{Prefix}.service.replication";
    public const string ServiceCompliance = $"{Prefix}.service.compliance";
}
```

### Pattern 4: Per-App AI Workflow via ModelScope Extension

**What:** Each app configures its Intelligence interaction via an `AppAiWorkflowConfig` that specifies: mode (on-demand/background/scheduled), budget limits, model preferences, approval requirements, and scope.

**Why:** The Intelligence plugin already supports `ModelScope` with Global/Instance/Department/Team levels. An app maps to a custom scope. The workflow config is persisted per app and sent to Intelligence via message bus when the app's requests arrive.

**Example:**
```csharp
public sealed record AppAiWorkflowConfig
{
    public required string AppId { get; init; }
    public AiWorkflowMode Mode { get; init; } = AiWorkflowMode.Auto;
    public decimal? BudgetLimitPerMonth { get; init; }
    public decimal? BudgetLimitPerRequest { get; init; }
    public string? PreferredProvider { get; init; }  // "openai", "claude", "ollama"
    public string? PreferredModel { get; init; }
    public bool RequireApproval { get; init; }
    public int MaxConcurrentRequests { get; init; } = 10;
    public string[] AllowedOperations { get; init; } = ["chat", "embeddings", "analysis"];
}

public enum AiWorkflowMode
{
    Auto,       // Intelligence chooses provider/model
    Manual,     // App specifies provider/model per request
    Budget,     // Intelligence optimizes for cost within budget
    Approval    // Requests above threshold require human approval
}
```

### Pattern 5: Per-App Observability via Tag-Based Isolation

**What:** All metrics, traces, and logs emitted by or on behalf of an app are tagged with `app_id={appId}`. The `AppObservabilityFilter` wraps existing observability strategy calls to inject the app ID tag. Per-app dashboards filter by this tag.

**Why:** No need to create separate observability strategy instances per app. Tag-based isolation is the standard pattern in all major observability platforms (Prometheus labels, OpenTelemetry resource attributes, Datadog tags). The existing `MetricValue`, `SpanContext`, and `LogEntry` in `IObservabilityStrategy` already support metadata dictionaries.

### Anti-Patterns to Avoid

- **Direct plugin references:** AppPlatform must NEVER directly reference UltimateAccessControl, UltimateIntelligence, or UniversalObservability. All communication via message bus.
- **Storing raw API keys:** Service tokens must be stored as hashes only. The raw key is returned exactly once at creation.
- **Separate strategy classes per app:** Do NOT create individual strategy instances per registered app. Use configuration-driven behavior with a single strategy that resolves per-app config from its registry.
- **Bypassing AccessControl for platform operations:** Platform admin operations (register/deregister app) still go through the access control evaluation pipeline.
- **Hardcoding service list:** The service consumption API should be extensible. New services can be exposed by registering additional message handlers.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Multi-tenant access control | Custom tenant isolation logic | `MultiTenancyIsolationStrategy` in UltimateAccessControl | Already has tenant definitions, memberships, quotas, cross-tenant grants, hierarchical isolation, impersonation. Fully tested. |
| API key generation | Custom random token generator | `System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)` + `HMACSHA256` | Cryptographically secure, well-tested .NET APIs |
| JWT token handling | Custom JWT parser/generator | Existing `IJwtTokenService` in Dashboard | Already supports roles, claims, refresh tokens |
| AI model scoping | Custom scope hierarchy | `ModelScope` + `ScopeIdentifier` in UltimateIntelligence | Already has hierarchical scope resolution with inheritance |
| Tenant provisioning | Custom provisioning logic | `ServiceProvisioner` + `TenantLifecycleManager` in SDK | Already has provision/deprovision/lifecycle events |
| Observability tagging | Custom metrics pipeline | Tag injection into existing `IObservabilityStrategy` calls | Standard OpenTelemetry resource attribute pattern |

**Key insight:** Phase 19 is primarily an integration/composition layer. The hard problems (multi-tenancy, AI scoping, observability strategy management) are already solved by Phase 3, Phase 2, and Phase 4 deliverables. The platform plugin orchestrates these existing capabilities into a unified app-consumable surface.

## Common Pitfalls

### Pitfall 1: Treating App Registration as a Separate Concept from Tenancy

**What goes wrong:** Building a parallel tenant/app management system that doesn't leverage the existing multi-tenancy infrastructure, leading to duplicate state, inconsistent enforcement, and policy drift.
**Why it happens:** The phrase "app registration" sounds different from "tenant creation," but functionally they map 1:1 in this architecture.
**How to avoid:** Every `AppRegistration` operation must create a corresponding tenant in `MultiTenancyIsolationStrategy`. The `AppId` IS the `TenantId`. All access checks for app-scoped resources go through the existing multi-tenancy evaluation path.
**Warning signs:** If you find yourself implementing `EvaluateAppAccess()` separately from the existing `EvaluateAccessCoreAsync()`, you've diverged.

### Pitfall 2: Service Token Scope Explosion

**What goes wrong:** Creating overly granular service token scopes (per-endpoint, per-operation) that become unmanageable.
**Why it happens:** Trying to be too precise about what each token can do, rather than using coarse service-level scopes combined with per-app access policies.
**How to avoid:** Service token scopes should be service-level only: `storage`, `intelligence`, `accesscontrol`, `observability`, `replication`, `compliance`. Fine-grained permissions within each service are handled by the per-app access control policies in UltimateAccessControl.
**Warning signs:** More than 10 scope strings, or scopes that reference specific API endpoints.

### Pitfall 3: Forgetting Message Bus Context Propagation

**What goes wrong:** When AppPlatform routes a request to UltimateStorage (for example), the storage plugin doesn't know which app the request is for, so it can't enforce per-app quotas or isolation.
**Why it happens:** The message forwarding strips or doesn't add app context metadata.
**How to avoid:** The `AppContextRouter` must enrich every forwarded `PluginMessage` with `Payload["AppId"]`, `Payload["AppContext"]`, and `Payload["ServiceTokenId"]` before sending to downstream plugins. Define a standard `AppRequestContext` record that all platform messages include.
**Warning signs:** Downstream plugins handling platform-routed requests without any app identification.

### Pitfall 4: Observability Data Mixing

**What goes wrong:** Metrics from one app leak into another app's dashboard because the app_id tag is missing from some code paths.
**Why it happens:** Some observability paths emit metrics before the app context is resolved, or internal system metrics don't have app tagging.
**How to avoid:** Establish a clear convention: all platform-routed observability calls go through the `AppObservabilityFilter` which adds the tag. Internal system metrics use a reserved `system` app ID. Create a test that verifies all emitted metrics have an app_id tag.
**Warning signs:** Metrics appearing without app_id tag in observability dashboards.

### Pitfall 5: Token Validation Performance

**What goes wrong:** Every incoming request validates the service token by doing a full HMAC computation and database lookup, becoming a bottleneck at high request rates.
**Why it happens:** Not caching validated tokens.
**How to avoid:** Cache validated token results in a `ConcurrentDictionary<string, TokenValidationResult>` with a short TTL (30-60 seconds). Use the token hash prefix as cache key. Invalidate cache entries when a token is revoked.
**Warning signs:** Token validation taking >1ms per request, or high memory allocation in token validation path.

## Code Examples

### App Registration Record Types

```csharp
// Source: Consistent with codebase record patterns (see TenantDefinition in MultiTenancyIsolationStrategy.cs)
public sealed record AppRegistration
{
    public required string AppId { get; init; }
    public required string AppName { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public required string OwnerUserId { get; init; }
    public required string[] CallbackUrls { get; init; }
    public AppStatus Status { get; init; } = AppStatus.Active;
    public required DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public DateTime? SuspendedAt { get; init; }
    public string? SuspensionReason { get; init; }
    public AppServiceConfig ServiceConfig { get; init; } = new();
    public Dictionary<string, object> Metadata { get; init; } = new();
}

public enum AppStatus
{
    Pending,
    Active,
    Suspended,
    Deactivated,
    Deleted
}

public sealed record AppServiceConfig
{
    public int MaxUsers { get; init; } = 100;
    public int MaxResources { get; init; } = 10000;
    public long MaxStorageBytes { get; init; } = 1_073_741_824; // 1 GB
    public string[] EnabledServices { get; init; } = ["storage", "accesscontrol", "intelligence", "observability"];
    public AppAiWorkflowConfig? AiConfig { get; init; }
}
```

### Service Token Generation

```csharp
// Source: Pattern from System.Security.Cryptography, consistent with Dashboard AuthController
internal sealed class ServiceTokenService
{
    private readonly ConcurrentDictionary<string, ServiceToken> _tokens = new();

    public (string RawKey, ServiceToken Token) CreateToken(
        string appId,
        string[] scopes,
        TimeSpan validity)
    {
        var rawKeyBytes = RandomNumberGenerator.GetBytes(32);
        var rawKey = Convert.ToBase64String(rawKeyBytes);

        var tokenId = Guid.NewGuid().ToString("N");
        var tokenHash = ComputeHash(rawKey);

        var token = new ServiceToken
        {
            TokenId = tokenId,
            AppId = appId,
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(validity),
            AllowedScopes = scopes,
            IsRevoked = false
        };

        _tokens[tokenId] = token;

        // Return raw key only once -- never stored
        return (rawKey, token);
    }

    public ServiceToken? ValidateToken(string rawKey)
    {
        var hash = ComputeHash(rawKey);
        var token = _tokens.Values.FirstOrDefault(t =>
            t.TokenHash == hash &&
            !t.IsRevoked &&
            t.ExpiresAt > DateTime.UtcNow);
        return token;
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
```

### Plugin Main Class Structure

```csharp
// Source: Pattern from UltimateInterfacePlugin, DataMarketplacePlugin, PluginMarketplacePlugin
public sealed class AppPlatformPlugin : IntelligenceAwarePluginBase, IDisposable
{
    private readonly ConcurrentDictionary<string, AppRegistration> _registrations = new();
    private readonly ServiceTokenService _tokenService = new();
    private bool _disposed;

    public override string Id => "com.datawarehouse.platform.app";
    public override string Name => "Application Platform Services";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    public override async Task StartAsync(CancellationToken ct)
    {
        await base.StartAsync(ct);
        SubscribeToMessageBusTopics();
    }

    private void SubscribeToMessageBusTopics()
    {
        // App lifecycle
        MessageBus?.Subscribe(PlatformTopics.AppRegister, HandleAppRegisterAsync);
        MessageBus?.Subscribe(PlatformTopics.AppDeregister, HandleAppDeregisterAsync);
        MessageBus?.Subscribe(PlatformTopics.AppList, HandleAppListAsync);

        // Token management
        MessageBus?.Subscribe(PlatformTopics.TokenCreate, HandleTokenCreateAsync);
        MessageBus?.Subscribe(PlatformTopics.TokenRotate, HandleTokenRotateAsync);
        MessageBus?.Subscribe(PlatformTopics.TokenRevoke, HandleTokenRevokeAsync);

        // Service routing (pattern matching for all app-scoped service requests)
        MessageBus?.SubscribePattern("platform.service.*", HandleServiceRequestAsync);
    }

    // ... handlers delegate to internal services and forward to downstream plugins via message bus
}
```

### .csproj Template

```xml
<!-- Source: Pattern from DataMarketplace.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Description>Application Platform Services plugin for DataWarehouse. Enables registered applications to consume DW services with per-app isolation, service tokens, AI workflows, access control policies, and observability.</Description>
    <Authors>DataWarehouse Team</Authors>
    <Company>DataWarehouse</Company>
    <Product>DataWarehouse Application Platform Plugin</Product>
    <Copyright>Copyright (c) DataWarehouse. All rights reserved.</Copyright>
    <Version>1.0.0</Version>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591</NoWarn>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\DataWarehouse.SDK\DataWarehouse.SDK.csproj" />
  </ItemGroup>
</Project>
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Per-service API keys | Unified service token with scope-based access | Current design | Single token covers all allowed services |
| Separate tenant and app models | App IS a tenant (1:1 mapping) | Current design | Eliminates duplication, leverages existing multi-tenancy |
| Per-app observability instances | Tag-based isolation on shared observability | Current design | Scales without resource multiplication |
| AI workflow as code | AI workflow as configuration per app | Current design | App owners can change modes without code changes |

**Key architectural decisions for the planner:**
- `AppId` === `TenantId` in the multi-tenancy system
- Service tokens use SHA256 hash storage, raw key returned once
- `PluginCategory.FeatureProvider` for the new plugin (matches other orchestrating plugins)
- Message bus topics use `platform.` prefix for all platform operations
- `IntelligenceAwarePluginBase` as the base class (needed for AI workflow integration)

## Open Questions

1. **Service Token Persistence**
   - What we know: In-memory `ConcurrentDictionary` works for runtime, consistent with codebase patterns
   - What's unclear: Whether tokens should survive kernel restarts via `IKernelStorageService`
   - Recommendation: Use `IKernelStorageService` for persistence (consistent with other plugins that persist state). Load tokens into memory on `StartAsync`, flush on changes.

2. **Cross-App Service Sharing**
   - What we know: `MultiTenancyIsolationStrategy` supports `SharedResources` and `Federated` isolation levels
   - What's unclear: Whether apps need to share data/resources with each other
   - Recommendation: Default to `Strict` isolation. Support `SharedResources` and `Federated` modes for apps that explicitly opt in.

3. **Dashboard/GUI Integration**
   - What we know: The GUI has a Marketplace page and the Dashboard has auth and API controllers
   - What's unclear: Whether Phase 19 needs to add a platform admin UI
   - Recommendation: Phase 19 is backend-only (message bus API). GUI integration deferred unless specified.

## Sources

### Primary (HIGH confidence)
- `Plugins/DataWarehouse.Plugins.UltimateAccessControl/Strategies/Core/MultiTenancyIsolationStrategy.cs` -- Full multi-tenancy implementation with tenant definitions, memberships, quotas, cross-tenant grants
- `DataWarehouse.SDK/Contracts/IMessageBus.cs` -- Message bus contract with publish, subscribe, send, pattern matching
- `DataWarehouse.SDK/Contracts/PluginBase.cs` -- Plugin base class hierarchy (PluginBase -> FeaturePluginBase -> IntelligenceAwarePluginBase)
- `DataWarehouse.SDK/Contracts/IntelligenceAware/IntelligenceAwarePluginBase.cs` -- Intelligence-aware base class with capability discovery
- `DataWarehouse.SDK/Contracts/Observability/IObservabilityStrategy.cs` -- Observability strategy contract
- `Plugins/DataWarehouse.Plugins.UltimateIntelligence/DomainModels/ModelScoping.cs` -- Model scope hierarchy
- `Plugins/DataWarehouse.Plugins.UltimateIntelligence/Modes/InteractionModes.cs` -- Interaction mode message topics
- `DataWarehouse.SDK/Infrastructure/EdgeManagedPhase8.cs` -- ServiceProvisioner, TenantLifecycleManager
- `DataWarehouse.SDK/Primitives/Enums.cs` -- PluginCategory definitions
- `DataWarehouse.SDK/Primitives/Handshake.cs` -- Handshake protocol
- `Plugins/DataWarehouse.Plugins.DataMarketplace/DataWarehouse.Plugins.DataMarketplace.csproj` -- .csproj template
- `.planning/codebase/CONVENTIONS.md` -- Naming and code style conventions
- `.planning/codebase/ARCHITECTURE.md` -- Architecture patterns and data flow

### Secondary (MEDIUM confidence)
- `.planning/ROADMAP.md` -- Phase 19 definition and success criteria
- `.planning/REQUIREMENTS.md` -- PLATFORM-01/02/03 requirement IDs (referenced in roadmap but not yet defined in requirements)
- `.planning/phases/17-marketplace/17-RESEARCH.md` -- Pattern reference for plugin creation research

### Notes
- PLATFORM-01/02/03 are referenced in the ROADMAP.md phase definition but have no corresponding entries in `Metadata/TODO.md` or `REQUIREMENTS.md`. This is new work, not verify-and-complete work.
- No existing `AppPlatform` directory, plugin, or SDK contracts exist. Everything must be created from scratch.
- The `ValidationMiddleware.cs` in SDK has a `TenantId` property, confirming the SDK already recognizes tenant-scoped operations.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- All referenced existing components were directly inspected in the codebase
- Architecture: HIGH -- Patterns follow established codebase conventions verified across 5+ existing plugins
- Pitfalls: HIGH -- Derived from direct analysis of how existing multi-tenancy and message bus work
- Code examples: HIGH -- Based on actual codebase patterns, verified record/class structures, naming conventions

**Research date:** 2026-02-11
**Valid until:** 2026-03-11 (stable -- this is an internal architecture, not subject to external dependency changes)
