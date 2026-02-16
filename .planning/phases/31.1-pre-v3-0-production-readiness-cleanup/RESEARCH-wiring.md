# Phase 31.1: Pre-v3.0 Production Readiness Cleanup - Wiring Gap Research

**Researched:** 2026-02-16
**Domain:** Plugin wiring, message bus integration, DI, build errors
**Confidence:** HIGH (direct code analysis of all 60 plugins)

## Summary

Deep scan of all 60 plugins under `Plugins/DataWarehouse.Plugins.*` revealed **14 build errors** across 2 plugins (known), **4 commented-out message bus integrations** (dead code paths), **1 plugin with a perpetually-null message bus**, **1 plugin defining its own IMessageBusClient interface** instead of using the SDK's IMessageBus, **2 strategies with placeholder message bus responses** that will always take the fallback path, and **several plugins that lack inter-plugin communication capabilities they should have**.

No cross-plugin `using` violations were found -- all plugins reference only their own namespaces and the SDK. No direct `new MessageBus()` construction was found. Most plugins correctly use `MessageBus` from their base class or accept it via constructor injection.

**Primary finding:** The wiring gaps fall into three categories: (1) build-breaking API mismatches, (2) message bus integration that was started but never connected, and (3) strategies that are permanently disconnected from intelligence because the plumbing was never completed.

---

## Category 1: Build Errors (14 errors, 2 plugins)

### 1.1 UltimateCompression - CS1729: Constructor Mismatch (12 errors)

**Severity:** BUILD-BREAKING
**Plugin:** `DataWarehouse.Plugins.UltimateCompression`
**Root Cause:** SharpCompress v0.45.1 changed the `LzmaStream` and `BZip2Stream` constructors from 3-argument to 2-argument signatures.

| File | Line(s) | Class | Error |
|------|---------|-------|-------|
| `Strategies/Archive/XzStrategy.cs` | 63, 139 | LzmaStream | 3-arg constructor no longer exists |
| `Strategies/Archive/SevenZipStrategy.cs` | 66, 111 | LzmaStream | 3-arg constructor no longer exists |
| `Strategies/Transform/Bzip2Strategy.cs` | 55, 73, 86, 95 | BZip2Stream | 3-arg constructor no longer exists |
| `Strategies/LzFamily/LzmaStrategy.cs` | 67, 112 | LzmaStream | 3-arg constructor no longer exists |
| `Strategies/LzFamily/Lzma2Strategy.cs` | 247, 272 | LzmaStream | 3-arg constructor no longer exists |

**Fix:** Update all `new LzmaStream(props, false, stream)` calls to match SharpCompress v0.45.1 API. Likely needs `new LzmaStream(props, stream)` with compress/decompress controlled differently.

### 1.2 AedsCore - CS0234: Namespace Not Found (1 error)

**Severity:** BUILD-BREAKING
**Plugin:** `DataWarehouse.Plugins.AedsCore`
**File:** `ControlPlane/MqttControlPlanePlugin.cs` line 6
**Root Cause:** MQTTnet v5.1.0 restructured namespaces. `MQTTnet.Client` no longer exists at that path.

```csharp
// Current (broken):
using MQTTnet.Client;

// MQTTnet v5.x moved client types - needs investigation of exact new namespace
```

**Fix:** Update `using` directives to match MQTTnet v5.x namespace structure.

---

## Category 2: Disconnected Message Bus Integration (4 instances)

### 2.1 TamperProof - Commented-Out PublishAsync Calls

**Severity:** HIGH - Security alerts silently dropped
**Plugin:** `DataWarehouse.Plugins.TamperProof`

The plugin explicitly documents that `IMessageBus` is **not injected** and alerts are not published:

```csharp
// TamperProofPlugin.cs:166-167
// Message bus integration: once IMessageBus is injected, publish violation events:
// await _messageBus.PublishAsync("tamperproof.background.violation", e, ct);

// TamperProofPlugin.cs:842-855
// Note: This requires access to IMessageBus which is not currently injected.
// For now, we'll log a warning that the feature needs message bus integration.
// In a complete implementation, IMessageBus should be added to constructor dependencies.
```

**Impact:** Background scan violations and tamper alert incidents are logged but never published to the message bus. Other plugins (UltimateCompliance, monitoring systems) that subscribe to `tamperproof.background.violation` and `tamperproof.alert.detected` will never receive events.

**The plugin does have a `MessageBusIntegrationService`** (in `Services/MessageBusIntegration.cs`) that wraps a custom `IMessageBusClient` interface, but:
1. It defines its own `IMessageBusClient` interface instead of using `SDK.Contracts.IMessageBus`
2. The `MessageBusIntegrationService` is **never instantiated** by `TamperProofPlugin`
3. The `TamperProofPlugin` constructor creates `TamperIncidentService`, `BlockchainVerificationService`, and `RecoveryService` but NOT the `MessageBusIntegrationService`

### 2.2 UltimateInterface - GenericWebhookStrategy Commented-Out Routing

**Severity:** MEDIUM
**Plugin:** `DataWarehouse.Plugins.UltimateInterface`
**File:** `Strategies/Conversational/GenericWebhookStrategy.cs` line 144

```csharp
// In production, this would send to the mapped topic
// await MessageBus.PublishAsync(topic, payload, cancellationToken);
```

Webhook events are received but never forwarded to the message bus. The event routing table exists but is non-functional.

### 2.3 FuseDriver - GetMessageBus Always Returns Null

**Severity:** MEDIUM
**Plugin:** `DataWarehouse.Plugins.FuseDriver`
**File:** `FuseDriverPlugin.cs` lines 722-727

```csharp
private IMessageBus? GetMessageBus(HandshakeRequest request)
{
    // Message bus may be provided via config or other mechanisms
    // For now, return null as message bus is optional
    return null;
}
```

The plugin declares `_messageBus` (line 44), assigns it from `GetMessageBus()` on handshake (line 114), but `GetMessageBus()` always returns null. Lines 680 and 691 check for null and bail out, meaning FUSE filesystem events never reach the message bus. Other InterfacePluginBase plugins (KubernetesCsi, WinFspDriver, UniversalDashboards) access `MessageBus` from the base class property -- FuseDriver should do the same instead of its custom `GetMessageBus()` method.

---

## Category 3: Permanently Disconnected Strategies (2 instances)

### 3.1 UltimateAccessControl - UebaStrategy Placeholder Response

**Severity:** MEDIUM - AI-enhanced security always falls back to rules
**Plugin:** `DataWarehouse.Plugins.UltimateAccessControl`
**File:** `Strategies/ThreatDetection/UebaStrategy.cs` line 178

```csharp
// Note: In production, would use message bus request-response pattern
// For now, simulate the pattern - Intelligence plugin integration will be completed separately
AnalysisResponse? aiResponse = null; // Placeholder for message bus response

if (aiResponse == null || !aiResponse.Success)
{
    _logger.LogWarning("Intelligence plugin unavailable for UEBA (Success=false), using rule-based fallback");
    return null;
}
```

`aiResponse` is **always null**, so this code **always returns null** and logs a warning. The AI-enhanced anomaly detection path is permanently dead code.

### 3.2 UltimateAccessControl - ThreatIntelStrategy Placeholder Response

**Severity:** MEDIUM - Same pattern as above
**Plugin:** `DataWarehouse.Plugins.UltimateAccessControl`
**File:** `Strategies/ThreatDetection/ThreatIntelStrategy.cs` line 197

```csharp
EnrichmentResponse? aiResponse = null; // Placeholder for message bus response
```

Same issue: threat intelligence enrichment via AI is always null, always falls back.

---

## Category 4: Custom Interface Instead of SDK Standard (1 instance)

### 4.1 TamperProof - Custom IMessageBusClient

**Severity:** MEDIUM - Architectural inconsistency
**Plugin:** `DataWarehouse.Plugins.TamperProof`
**File:** `Services/MessageBusIntegration.cs` lines 394-409

The plugin defines its own `IMessageBusClient` interface:

```csharp
public interface IMessageBusClient
{
    Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string topic, TRequest request, TimeSpan timeout, CancellationToken ct = default);
    Task PublishAsync<T>(string topic, T message, CancellationToken ct = default);
}
```

This is a subset of `DataWarehouse.SDK.Contracts.IMessageBus`. There is no adapter/bridge between the two. If/when the message bus is wired up, this custom interface would need to either be replaced with the SDK interface or an adapter created.

---

## Category 5: Plugins Without Message Bus Communication

These plugins have **no message bus usage at all** but probably should for a production system:

| Plugin | Base Class | Why MB Would Be Useful |
|--------|-----------|------------------------|
| `KubernetesCsi` | InterfacePluginBase | Volume lifecycle events, capacity alerts |
| `DataMarketplace` | PlatformPluginBase | Listing notifications, purchase events |
| `Virtualization.SqlOverObject` | DataVirtualizationPluginBase | Query events, schema change notifications |
| `Raft` | ConsensusPluginBase | Leader election events, cluster state changes |
| `AirGapBridge` | InfrastructurePluginBase | Sync status, device detection events |
| `Compute.Wasm` | WasmFunctionPluginBase | Function execution events, resource usage |
| `Transcoding.Media` | MediaTranscodingPluginBase | Transcoding progress, completion events |

**Note:** These may be intentionally standalone (especially Raft, which handles consensus internally). Evaluate on a case-by-case basis during planning. KubernetesCsi does handle messages via `OnMessageAsync` using message types like `csi.controller.create_volume` -- it just never publishes events back.

---

## Category 6: Obsolete API Usage (Low Severity)

### Active [Obsolete] Declarations (informational, not broken)

| Plugin | File | What's Obsolete | Replacement |
|--------|------|----------------|-------------|
| UltimateKeyManagement | `PasswordDerivedPbkdf2Strategy.cs:376` | SHA1 for PBKDF2 | SHA256/SHA512 |
| UltimateStorage | Multiple S3-like strategies | AWS Sig V2 | AWS Sig V4 |
| UltimateRAID | `RaidPluginMigration.cs:410` | Legacy RAID plugins | IRaidStrategy directly |
| UltimateReplication | `UltimateReplicationPlugin.cs:96` | Legacy replication plugins | UltimateReplication |

These are **declaration-side** obsolete markers (the methods themselves are marked obsolete to warn callers). They are not broken but indicate migration debt.

---

## Category 7: DI/Service Construction Patterns

Most plugins construct their internal services manually with `new` rather than DI. This is the **expected pattern** in this architecture since plugins are loaded dynamically and manage their own internal dependencies. Key examples:

- `AirGapBridgePlugin`: Creates `PackageManager`, `StorageExtensionProvider`, `PocketInstanceManager`, `SecurityManager`, `ConvergenceManager` via `new`
- `AppPlatformPlugin`: Creates `AppRegistrationService`, `ServiceTokenService`, etc. passing `MessageBus!` and `Id`
- `TamperProofPlugin`: Creates `TamperIncidentService`, `BlockchainVerificationService`, `RecoveryService` via `new`
- `UltimateCompliancePlugin`: Creates `ComplianceReportService`, `ComplianceDashboardProvider`, `ComplianceAlertService`, `TamperIncidentWorkflowService` via `new`

**Assessment:** This is NOT a wiring gap. The microkernel architecture expects plugins to manage their own internal composition. These services are internal to each plugin and not shared.

---

## Category 8: Strategy Registration / Discovery

Strategies are registered in two patterns:
1. **Plugin-managed dictionaries** - plugins manually `new` strategies and add them to `_strategies` dictionaries during `OnHandshakeAsync`
2. **Base-class auto-discovery** - some plugin bases use reflection to find `StrategyBase` subclasses

Most plugins use pattern 1. **No missing strategy registration issues were found** -- all strategy files found in the plugins correspond to registered strategies in their parent plugin's initialization code or are internal helper classes not meant for registration.

---

## Category 9: ConfigureIntelligence Propagation

Several plugins correctly call `ConfigureIntelligence(MessageBus)` on their strategies after loading:
- UltimateDeployment, UltimateDataFormat, UltimateDataTransit, UltimateStorageProcessing, UltimateCompute, UltimateDatabaseProtocol, UltimateDataProtection, UltimateIoTIntegration, UltimateMultiCloud, UltimateResilience

**Plugins that load strategies but do NOT call ConfigureIntelligence:**
These would need investigation during planning to determine if their strategies need message bus access:
- UltimateAccessControl (strategies accept messageBus in constructor directly, not via ConfigureIntelligence)
- UltimateCompliance (strategies are ComplianceStrategyBase, may not need it)
- UltimateEncryption, UltimateStorage, UltimateRAID, UltimateReplication (check if strategies use MessageBus)

---

## Summary Table

| # | Category | Plugin(s) | Severity | Fix Complexity |
|---|----------|-----------|----------|---------------|
| 1.1 | Build Error CS1729 | UltimateCompression | BUILD-BREAKING | Medium - API migration |
| 1.2 | Build Error CS0234 | AedsCore | BUILD-BREAKING | Low - namespace update |
| 2.1 | Disconnected MB | TamperProof | HIGH | Medium - wire IMessageBus, remove custom interface |
| 2.2 | Disconnected MB | UltimateInterface | MEDIUM | Low - uncomment + test |
| 2.3 | Disconnected MB | FuseDriver | MEDIUM | Low - use base class MessageBus property |
| 3.1 | Dead Code Path | UltimateAccessControl (UEBA) | MEDIUM | Medium - implement MB request-response |
| 3.2 | Dead Code Path | UltimateAccessControl (ThreatIntel) | MEDIUM | Medium - implement MB request-response |
| 4.1 | Custom Interface | TamperProof | MEDIUM | Medium - replace with SDK IMessageBus |

**Total unique issues: 8 actionable items across 5 plugins**

---

## Negative Findings (What Was NOT Found)

- **No cross-plugin `using` references** -- all plugins import only their own namespaces and SDK
- **No direct `new MessageBus()` construction** -- message bus is always resolved from base class or injected
- **No missing abstract method overrides** -- all required overrides are implemented (verified via build)
- **No broken strategy registration** -- all strategy files correspond to registered strategies
- **No direct plugin-to-plugin calls** -- all inter-plugin communication goes through OnMessageAsync/PublishAsync

---

## Sources

- Direct code analysis via `dotnet build DataWarehouse.slnx` (build errors)
- Grep of all 60 plugin directories for MessageBus, _messageBus, ConfigureIntelligence, [Obsolete], cross-plugin references
- Manual review of TamperProofPlugin.cs, FuseDriverPlugin.cs, KubernetesCsiPlugin.cs, UebaStrategy.cs, ThreatIntelStrategy.cs, MessageBusIntegration.cs, StrategyBase.cs
