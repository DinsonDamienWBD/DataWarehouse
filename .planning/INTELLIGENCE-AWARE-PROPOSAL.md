# Intelligence-Aware Plugin Classification Proposal

**Date:** 2026-02-17
**Context:** The base class hierarchy currently forces ALL plugins through `IntelligenceAwarePluginBase`, carrying 23 AI helper methods, 14 private fields, and a 500ms T90 discovery penalty per plugin. This proposal classifies which plugins genuinely need intelligence awareness vs which should use a lighter `SimplePluginBase`.

**Design Principle:** "AI-native, not AI-bolted-on" — T90 should observe and understand domain decisions in real-time. But orchestrators that merely route to already-observable Ultimate plugins don't need their own AI surface.

**Decision Criteria:**
- **IntelligenceAware** = Plugin makes domain-specific decisions that T90 should observe, learn from, or influence (strategy selection, anomaly detection, pattern recognition, data classification, optimization)
- **SimplePlugin** = Plugin is a pass-through, adapter, interface layer, or orchestrator that delegates to IntelligenceAware plugins. T90 gains nothing from observing it directly.

---

## Category A: MUST Be IntelligenceAware (39 plugins)

These plugins own domain logic, strategy selection, or data transformation that T90 needs to observe.

### Core Data Pipeline (T90 observes data flow decisions)
| Plugin | Justification |
|--------|---------------|
| UltimateStorage | Strategy selection (local vs cloud vs VDE), failover decisions, performance tuning |
| UltimateFilesystem | Driver selection (mmap vs io_uring vs FUSE), block allocation, cache decisions |
| UltimateCompression | Algorithm selection per data type, compression ratio optimization |
| UltimateEncryption | Algorithm selection, key lifecycle, cascade decisions |
| UltimateDataTransit | In-transit encryption/compression decisions, transport selection |
| UltimateReplication | Replication strategy selection, conflict resolution, lag monitoring |
| UltimateRAID | Parity strategy selection, rebuild priority, failure pattern detection |
| UltimateDataIntegrity | Checksum strategy selection, corruption detection |
| UltimateDatabaseStorage | Database engine selection (Postgres vs Redis vs Consul), query optimization |
| UltimateDatabaseProtocol | Wire protocol selection, connection pooling decisions |

### Compute & Processing (T90 observes compute decisions)
| Plugin | Justification |
|--------|---------------|
| UltimateCompute | Runtime selection (WASM vs Spark vs native), locality placement, job scheduling |
| Compute.Wasm | Function sandboxing decisions, resource limit tuning, hot reload triggers |
| UltimateStorageProcessing | Near-data compute decisions, processing pipeline optimization |
| UltimateEdgeComputing | Edge vs cloud decision making, offline queue management, inference triggers |
| UltimateStreamingData | Backpressure mode selection, window strategy, checkpoint decisions |

### Security & Governance (T90 observes policy decisions)
| Plugin | Justification |
|--------|---------------|
| UltimateAccessControl | Access decisions, risk scoring, anomaly detection in access patterns |
| UltimateKeyManagement | Key rotation decisions, HSM selection, break-glass authorization |
| UltimateDataGovernance | Compliance enforcement, data classification, retention decisions |
| UltimateCompliance | Regulatory strategy selection, geofencing decisions, attestation |
| UltimateDataPrivacy | PII detection, anonymization strategy selection, consent enforcement |
| UltimateDataProtection | Backup strategy, recovery point decisions |
| TamperProof | Seal decisions, integrity chain management, WORM policy |
| UltimateBlockchain | Consensus participation, chain verification decisions |

### Data Management (T90 observes data lifecycle decisions)
| Plugin | Justification |
|--------|---------------|
| UltimateDataCatalog | Asset discovery, classification, search optimization |
| UltimateDataLineage | Lineage tracking, blast radius analysis, provenance decisions |
| UltimateDataQuality | Quality scoring, validation rule selection, anomaly detection |
| UltimateDataFormat | Format detection, conversion strategy selection |
| UltimateDataLake | Tiering decisions, partition strategy, compaction |
| UltimateDataManagement | Lifecycle policy enforcement, archival decisions |
| UltimateDataIntegration | ETL/ELT strategy selection, schema mapping decisions |

### Connectivity & IoT (T90 observes connection/device decisions)
| Plugin | Justification |
|--------|---------------|
| UltimateConnector | Protocol selection, circuit breaker decisions, connection health |
| UltimateIoTIntegration | Device management, ingestion strategy, anomaly detection on sensor data |
| UltimateDataFabric | Topology decisions, mesh/federation routing |
| UltimateDataMesh | Domain boundary decisions, data product routing |

### Intelligence & Observability (T90 core)
| Plugin | Justification |
|--------|---------------|
| UltimateIntelligence | T90 itself — the intelligence engine. Must be IntelligenceAware by definition (actually inherits from lower base) |
| UniversalObservability | Metrics, health, tracing — T90's eyes into the system |
| UltimateResilience | Failure detection, circuit breaker patterns, recovery orchestration |
| UltimateResourceManager | Resource allocation, memory/CPU decisions |
| UltimateSustainability | Energy-aware scheduling, carbon footprint optimization |

### Distributed Systems (T90 observes cluster decisions)
| Plugin | Justification |
|--------|---------------|
| Raft | Leader election, log replication, cluster health — critical state T90 should track |

---

## Category B: SimplePluginBase Candidates (22 plugins)

These are orchestrators, adapters, interface layers, or infrastructure pass-throughs. They delegate to Category A plugins that are already observable.

### Interface / UI Layers (no domain decisions — present data from observable plugins)
| Plugin | Justification |
|--------|---------------|
| UltimateInterface | CLI/GUI/REST/GraphQL endpoint routing — purely presents data |
| UniversalDashboards | Dashboard rendering — consumes observability data, makes no domain decisions |

### Pure Orchestrators (route to Ultimate plugins that are already observable)
| Plugin | Justification |
|--------|---------------|
| UltimateWorkflow | Orchestrates steps across other plugins. The called plugins are already observable. Workflow itself is routing logic. |
| UltimateMicroservices | Service mesh routing, discovery delegation — infrastructure routing |
| UltimateServerless | Function dispatch — delegates compute to UltimateCompute |
| UltimateMultiCloud | Cloud provider abstraction — routes to storage/compute plugins |
| UltimateDeployment | Deployment orchestration — infrastructure, not data domain |

### Platform Adapters (OS/driver bridges — mechanical translation, no domain decisions)
| Plugin | Justification |
|--------|---------------|
| FuseDriver | FUSE filesystem bridge — mechanical translation between FUSE callbacks and UltimateFilesystem |
| WinFspDriver | WinFSP filesystem bridge — same as FuseDriver for Windows |
| KubernetesCsi | CSI driver bridge — translates K8s CSI calls to storage plugin calls |
| UltimateRTOSBridge | RTOS bridge — mechanical interop layer |
| UltimateSDKPorts | SDK port adapters — compatibility layer |
| Virtualization.SqlOverObject | SQL protocol adapter — translates SQL to object store calls |

### Content Processors (format-specific, no strategic decisions)
| Plugin | Justification |
|--------|---------------|
| Transcoding.Media | Media transcoding — mechanical format conversion |
| UltimateDocGen | Document generation — template rendering |

### Transport Adapters (wire protocol bridges)
| Plugin | Justification |
|--------|---------------|
| AdaptiveTransport | Transport selection is mechanical (latency-based), not domain-aware |
| AirGapBridge | Air-gap transfer — mechanical data ferry |
| AedsCore | Distributed sync protocol — transport layer, not domain decisions |

### Specialized / Experimental
| Plugin | Justification |
|--------|---------------|
| SelfEmulatingObjects | Object self-description — metadata layer |
| DataMarketplace | Data marketplace — commerce layer on top of observable catalog |
| PluginMarketplace | Plugin distribution — infrastructure |

---

## Borderline Cases (Could Go Either Way)

These need team discussion:

| Plugin | Argument FOR Intelligence | Argument AGAINST |
|--------|--------------------------|-----------------|
| **UltimateWorkflow** | Workflow decisions (retry/skip/compensate) could benefit from AI optimization | It orchestrates already-observable plugins; AI could observe at plugin level |
| **AedsCore** | Sync conflict resolution could benefit from AI | Transport-layer protocol, not domain |
| **AdaptiveTransport** | Transport selection could be AI-optimized | Currently rule-based latency selection |
| **Transcoding.Media** | Quality/codec selection could be AI-optimized | Currently mechanical format conversion |
| **DataMarketplace** | Pricing/recommendation could be AI-driven | Currently commerce infrastructure |
| **SelfEmulatingObjects** | Self-description is inherently intelligence-related | Metadata reflection, not AI |

---

## Implementation Plan

### Step 1: Create SimplePluginBase
```
PluginBase (lifecycle only: Initialize, Start, Stop, Shutdown, Handshake)
  -> SimplePluginBase (message bus, basic knowledge registration, NO AI methods)
  -> IntelligenceAwarePluginBase (full 23 AI methods, T90 discovery, knowledge cache)
```

`SimplePluginBase` keeps:
- Plugin lifecycle (Initialize, Activate, Execute, Shutdown)
- Message bus communication (subscribe/publish)
- Basic capability registration (so T90 knows the plugin exists and what it does)
- Knowledge registration (so T90 can query the plugin's static knowledge)

`SimplePluginBase` drops:
- 23 AI helper methods (embeddings, classification, anomaly detection, etc.)
- `_pendingRequests` ConcurrentDictionary
- `_intelligenceSubscriptions` list
- T90 discovery on startup (no 500ms penalty)
- Intelligence state tracking (`_isIntelligenceAvailable`, etc.)

### Step 2: Fix T90 Discovery (Even for IntelligenceAware Plugins)
- **Global discovery cache:** First plugin discovers T90, result cached statically. All subsequent plugins read the cache (0ms).
- **Reduce timeout to 50ms** with background retry every 5s.
- **Lazy discovery:** Don't discover on `StartAsync`. Discover on first actual AI method call.

### Step 3: Migrate Category B Plugins
Change base class for the 22 Category B plugins from `IntelligenceAwarePluginBase` hierarchy to `SimplePluginBase` hierarchy. This is a mechanical change — remove unused AI method overrides (there shouldn't be any in orchestrators).

### Impact Estimate
- **Startup time saved:** 22 plugins x 500ms = **11 seconds** eliminated
- **Memory saved:** 22 plugins x ~1.5KB = **~33KB** (modest but cleaner)
- **GC pressure reduced:** 22 fewer T90 broadcast subscribers, 22 fewer DiscoverResponse listeners
- **Remaining IntelligenceAware plugins benefit from:** global discovery cache + reduced timeout = **~500ms total** instead of 39 x 500ms = 19.5 seconds

### Net Effect on Startup
- **Before:** 61 plugins x 500ms = 30.5 seconds of T90 discovery alone
- **After:** 1 global discovery (50ms) + 0ms for all others = **50ms total**

**Owner directive:** Implement to 100% production ready state with maximum efficiency and performance as the goal.

---

# AI Provider Subscription Model

## Design Overview

DataWarehouse does NOT ship with or require any AI subscription. AI is optional, pluggable, and configurable at multiple levels. The system is fully functional without AI — all operations have manual fallbacks. When AI is configured, the system becomes "AI-native" through T90's observation and optimization of plugin behavior.

## Subscription Configuration Levels

### Level 1: System-Wide (Organization Level)
- **Configured by:** Organization Administrator
- **Scope:** ALL instances across the entire organization
- **Example:** Organization subscribes to Microsoft Copilot for all DataWarehouse instances
- **Use case:** Company has an enterprise AI agreement that covers all workloads

### Level 2: Instance Level
- **Configured by:** Instance Administrator
- **Scope:** One specific DataWarehouse instance
- **Example:** Financial instance uses Claude, Document storage uses ChatGPT, Blueprint storage uses Copilot
- **Overrides:** Instance-level configuration overrides system-wide for that instance
- **Use case:** Different data sensitivity levels require different AI providers (e.g., sovereign AI for classified data)

### Level 3: User Level
- **Configured by:** Individual non-admin user
- **Scope:** That user's own AI-assisted operations only
- **Example:** User adds their personal Claude Max subscription
- **Does NOT affect:** System/background tasks, other users' tasks

## Provider Configuration Model

```
AIProviderConfig:
  ProviderId: string              # "claude", "openai", "gemini", "copilot", "ollama", etc.
  DisplayName: string             # "Claude 4 Sonnet", "GPT-5", etc.
  EndpointUrl: string             # API endpoint
  ApiKey: SecureString            # Encrypted at rest, pinned in memory
  ModelId: string                 # Specific model to use
  MaxTokensPerMinute: int?        # Rate limit (null = provider default)
  MaxCostPerMonth: decimal?       # Budget cap (null = unlimited)
  SupportedCapabilities: string[] # ["embeddings", "classification", "completion", "vision", etc.]
  Priority: int                   # Lower = preferred (for fallback ordering)
```

## Resolution Logic (Which Subscription Handles Which Task)

### Scenario A: No AI subscription at any level
- System is fully functional without AI
- All management tasks performed manually by admin
- All user tasks performed manually
- Knowledge bank still works (stores manual observations, not AI-generated insights)
- No degradation — AI features simply don't activate

### Scenario B: System/Instance subscription only, no user subscriptions
- **Background/system tasks:** Use system/instance subscription
  - Auto-optimization, anomaly detection, predictive maintenance, etc.
- **User-initiated AI tasks:** Use system/instance subscription
  - Smart search, data classification suggestions, query optimization, etc.
- **Cost:** All AI costs borne by the organization

### Scenario C: No system subscription, some users have subscriptions
- **Background/system tasks:** Run manually (no AI available for system tasks)
  - Admin must manually perform optimization, monitoring, etc.
- **Users WITH subscriptions:** Can run user-level AI tasks using their own subscription
- **Users WITHOUT subscriptions:** Must perform tasks manually
- **Cost:** Each user bears their own AI costs

### Scenario D: System subscription + some user subscriptions (Full Model)
- **Background/system tasks:** Always use system/instance subscription
- **Users WITHOUT own subscription:** Automatically use system/instance subscription
- **Users WITH own subscription:**
  1. Presented with dropdown: "Use system subscription" or "Use my subscription"
  2. Asked: "Apply this choice for this time only, or set as default?"
  3. User can change their default at any time in settings
  4. **Automatic fallback:** If user's own subscription hits rate limit, automatically falls back to system subscription until user's tokens renew
  5. **Notification:** User is notified when fallback occurs, with estimated time until their tokens renew

## Fallback Chain (Priority Order)

For any AI task:
```
1. User's explicitly selected provider (if user has configured one and selected it as default)
   ↓ (if rate limited or unavailable)
2. Instance-level provider (if configured for this instance)
   ↓ (if rate limited or unavailable)
3. System-wide provider (if configured for the organization)
   ↓ (if rate limited or unavailable)
4. Manual fallback (task queued for human action, or skipped if non-critical)
```

## Background Task AI Cost Model

For background tasks that consume AI tokens (anomaly detection, predictive optimization, etc.):

1. **Before first run:** User/admin is notified with estimated cost
2. **AI runs once:** Performs analysis, generates findings
3. **Results cached in Knowledge Bank:** Findings, patterns, and solutions stored persistently
4. **Next occurrence of same/similar issue:** System checks Knowledge Bank FIRST
   - If solution found in Knowledge Bank → no AI call needed (zero cost)
   - If no match → AI runs again, results cached
5. **Knowledge Bank consolidation:** Periodic AI-driven consolidation of learnings (scheduled, cost-estimated, admin-approved)

## Knowledge Bank Integration

The Knowledge Bank (via `IKnowledgeLake`) serves as the persistent memory that reduces AI costs over time:

```
Write flow:  AI generates insight → Store in Knowledge Bank with tags/embeddings
Read flow:   New problem detected → Search Knowledge Bank by similarity
             → Match found? Return cached solution (no AI cost)
             → No match? Route to AI provider (costs tokens, result cached)
```

This creates a **self-improving system** where AI costs decrease over time as the Knowledge Bank grows. Organizations that run longer accumulate more knowledge and need fewer AI calls.

## Multi-Provider Complementary Mode

When multiple providers are configured (e.g., system has Copilot, user has Claude):
- Providers are **complementary, not conflicting**
- Each provider may have different strengths (embeddings vs completion vs vision)
- Task routing considers provider capabilities:
  - Embedding task → route to provider with `"embeddings"` capability
  - Vision task → route to provider with `"vision"` capability
  - If multiple providers support the same capability → use the one selected by user preference / priority ordering
- No provider is called for tasks outside its capability set

## Configuration UI Flow

### Admin Configuration
```
Settings > AI Providers > System-Wide / Instance
  [+ Add Provider]
    Provider: [Claude | OpenAI | Gemini | Copilot | Ollama | Custom...]
    API Key: [••••••••••••]
    Model: [Auto-detect | Select specific...]
    Budget Cap: [$ per month]
    Scope: [All Instances | Select Instances...]

  Active Providers:
    1. Microsoft Copilot (System-wide) — $X/month used
    2. Claude Sonnet (Finance Instance only) — $Y/month used
```

### User Configuration
```
Settings > AI Preferences
  System AI: Microsoft Copilot (configured by admin)
  My AI: [+ Add my own provider]

  Default for my tasks: [System AI ▼ | My AI ▼]
  Apply: [This session | Always]

  [x] Auto-fallback to system AI when my tokens are exhausted
  [ ] Notify me before each AI task with cost estimate
```

## Security Requirements

- API keys encrypted at rest using the configured encryption strategy (via UltimateEncryption)
- API keys pinned in memory (per FIX-018 PinnedKeyHandle pattern)
- User-level API keys isolated — no user can see or use another user's key
- Admin can see which providers are configured (not the keys themselves)
- All AI API calls logged in audit trail (provider, model, token count, cost, user, timestamp)
- Rate limit tracking per provider per user (stored in observability metrics)
- Budget enforcement: hard stop when monthly cap reached (configurable: hard stop vs warning only)

---

# Current Wiring Status (Audit of Existing Implementation)

## What EXISTS and Is Production-Quality

| Component | Location | Status |
|-----------|----------|--------|
| `IAIProvider` interface | `SDK/AI/IAIProvider.cs` | Complete — `CompleteAsync`, `CompleteStreamingAsync`, `GetEmbeddingsAsync`, `GetEmbeddingsBatchAsync` |
| `AICapabilities` flags enum | Same file | Complete — TextCompletion, ChatCompletion, Streaming, Embeddings, ImageGeneration, ImageAnalysis, FunctionCalling, CodeGeneration |
| `IAIProviderRegistry` interface | Same file | Complete interface — **no concrete implementation** |
| 7 concrete AI provider strategies | `UltimateIntelligence/Strategies/Providers/` | **All REAL HTTP implementations** — Claude, OpenAI, Gemini, Azure OpenAI, AWS Bedrock, HuggingFace, Ollama |
| Multi-provider registry in T90 | `UltimateIntelligencePlugin.cs` | Real — auto-discovers all strategies via reflection, registers simultaneously |
| Provider selection by cost/latency | `SelectBestAIProvider()` | Real — ranks by `CostTier` or `LatencyTier` |
| `AICredential` model (BYOK + OAuth) | `DataWarehouse.Shared/Models/AICredential.cs` | Complete — ProviderId, Scope (Personal/Organization/Instance), ApiKey, OAuthToken, TenantId, Priority, IsEnabled, ExpiresAt |
| `IUserCredentialVault` + `UserCredentialVault` | `DataWarehouse.Shared/Services/` | Complete — AES-256-GCM encryption, user-specific key derivation, OAuth token refresh, credential rotation, access auditing |
| Three-tier scope resolution | `GetEffectiveCredentialAsync()` | Complete — Personal → Organization → Instance fallback |
| `QuotaTier` system | `UltimateIntelligence/Quota/QuotaManagement.cs` | Complete — Free/Basic/Pro/Enterprise/BYOK with default limits |
| `QuotaManager` + `RateLimiter` | Same file | Complete — pre-request check, post-request recording, sliding window rate limiter |
| `CostEstimator` with pricing table | Same file | Complete — GPT-3.5/4/4o, Claude Instant/2/3 Haiku/Sonnet/Opus, Gemini Pro |
| `IKnowledgeLake` interface | `SDK/Contracts/IKnowledgeLake.cs` | Complete — Store, Query, GetByTopic, TTL support, temporal queries |
| `KnowledgeObject` model | `SDK/AI/KnowledgeObject.cs` | Complete — Topic, SourcePluginId, KnowledgeType, Confidence, Tags, RelatedKnowledgeIds, ExpiresAt |
| IntelligenceAwarePluginBase discovery | `SDK/Contracts/IntelligenceAware/` | Complete — message bus discovery, 23 helper methods |

## What Is NOT Wired (The Missing Connector)

The central gap is a **request-time credential and quota resolver**. Today's execution path:

```
Plugin.RequestCompletionAsync()
  → message bus → T90.OnMessageAsync()
  → _activeAIProvider.CompleteAsync(request)
  → ClaudeProviderStrategy.CompleteAsync()
  → GetRequiredConfig("ApiKey")   ← reads from in-memory dict, NOT from vault
  → HttpClient.SendAsync(...)
```

**Missing pieces:**

| Gap | What's Needed | Exists? |
|-----|--------------|---------|
| Request-time credential resolver | Before each AI call: resolve `(userId, providerId)` → credential via `GetEffectiveCredentialAsync` | NOT wired — vault exists but strategies don't consult it |
| Pre-request quota check | Before each AI call: `QuotaManager.CanMakeRequestAsync(userId, model, tokens)` | NOT wired — QuotaManager exists but has zero callers |
| Post-request usage recording | After each AI call: `QuotaManager.RecordUsageAsync(userId, model, promptTokens, completionTokens)` | NOT wired — method exists but never called |
| Cost notification before background tasks | Estimate cost, notify user/admin, await approval | NOT designed or implemented |
| Auto-failover between providers | If active provider fails/rate-limited, try next by priority | NOT implemented — one active provider, no retry chain |
| User context in AI request path | `AIRequest` carries userId so credential/quota resolution is per-user | `AIRequest` has no userId field |
| Knowledge Bank as AI response cache | Store AI findings, check KB before calling AI again | `IKnowledgeLake` is a capability registry, NOT an AI response cache — needs extension |
| `IAIProviderRegistry` concrete implementation | Wire registry to T90's strategy collection | Interface defined, no impl |
| `IKnowledgeLake` concrete implementation | Persistent backend for knowledge storage | Interface defined, no impl |
| Persistent auth provider | Replace `InMemoryAuthProvider` with database-backed | Only in-memory dev implementation |
| User preference storage | "Use my provider as default" / "Use system provider" preference | NOT implemented |
| Rate limit fallback | When user hits rate limit, auto-fall back to system subscription | NOT implemented |

## Implementation Required

To fulfill the owner's AI subscription model (Scenarios A-D), the following must be built:

### 1. Add `userId` to AI Request Path
- Add `string? UserId` and `CredentialScope? PreferredScope` to `AIRequest`
- `IntelligenceAwarePluginBase` helper methods must accept and propagate userId

### 2. Build Request-Time Credential Resolver
- New class: `AIRequestResolver` in UltimateIntelligence
- On every AI request: resolve credential from vault, check quota, inject API key
- Fallback chain: User preference → Instance credential → System credential → Manual fallback

### 3. Wire QuotaManager into T90's Request Path
- Before `_activeAIProvider.CompleteAsync()`: call `QuotaManager.CanMakeRequestAsync`
- After response: call `QuotaManager.RecordUsageAsync`
- On quota exceeded: return error with `Reason` and `RetryAfter`

### 4. Build Auto-Failover
- If active provider returns rate-limit error (429): try next provider by priority
- If user's provider is rate-limited: fall back to system provider (per owner's Scenario D)
- Notify user when fallback occurs

### 5. Extend Knowledge Bank for AI Response Caching
- Add `KnowledgeType = "ai_response_cache"` with: query hash, response, model, confidence, timestamp
- Before any AI call: search KB for matching query hash
- If found and not expired: return cached response (zero AI cost)
- After AI call: store response in KB

### 6. Build Persistent Backends
- `IKnowledgeLake` → RocksDB or SQLite implementation
- `IIntelligenceAuthProvider` → database-backed (via UltimateDatabaseStorage)
- User preference storage → per-user configuration in credential vault

### 7. Build Cost Notification System
- For background tasks: estimate cost, publish `ai.cost.estimate` message
- Admin/user can approve or reject via message bus response
- Configurable: auto-approve under threshold, manual approve above
