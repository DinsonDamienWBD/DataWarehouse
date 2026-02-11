---
phase: 19-app-platform
plan: 03
subsystem: AppPlatform AI Workflow
tags: [ai-workflow, budget-enforcement, concurrency, per-app-config, intelligence-routing]
dependency_graph:
  requires:
    - "19-01 (AppPlatform core: registration, tokens, plugin base)"
  provides:
    - "Per-app AI workflow configuration (4 modes: Auto/Manual/Budget/Approval)"
    - "Budget enforcement (monthly + per-request limits)"
    - "Concurrency limiting with Interlocked operations"
    - "AI request routing to UltimateIntelligence via message bus"
    - "Usage tracking with automatic monthly period reset"
  affects:
    - "AppPlatformPlugin.cs (7 new topic handlers + strategy field)"
    - "PlatformTopics.cs (7 new AI workflow topic constants)"
tech_stack:
  added: []
  patterns:
    - "ConcurrentDictionary + Interlocked for thread-safe concurrency tracking"
    - "Record-based immutable config with 'with' expressions for updates"
    - "Message bus SendAsync for Intelligence communication"
    - "ParseDecimal helper for multi-type payload extraction"
key_files:
  created:
    - "Plugins/DataWarehouse.Plugins.AppPlatform/Models/AppAiWorkflowConfig.cs"
    - "Plugins/DataWarehouse.Plugins.AppPlatform/Strategies/AppAiWorkflowStrategy.cs"
  modified:
    - "Plugins/DataWarehouse.Plugins.AppPlatform/AppPlatformPlugin.cs"
    - "Plugins/DataWarehouse.Plugins.AppPlatform/Models/PlatformTopics.cs"
decisions:
  - "Used ConcurrentDictionary<string, int> + Interlocked for concurrent request counts (separate from immutable AiUsageTracking record)"
  - "Automatic monthly period reset when period start is in a previous month"
  - "Approval enforcement compares EstimatedCost from request payload against BudgetLimitPerRequest"
  - "Usage lock object for thread-safe spend/count updates (records are immutable, require replacement)"
metrics:
  duration: "5 min"
  completed: "2026-02-11"
---

# Phase 19 Plan 03: Per-App AI Workflow Configuration Summary

Per-app AI workflow config with 4 modes (Auto/Manual/Budget/Approval), budget enforcement (monthly + per-request), concurrency limiting via Interlocked, and Intelligence routing via message bus.

## Tasks Completed

### Task 1: Create AppAiWorkflowConfig model and AppAiWorkflowStrategy
**Commit:** e2a0246

Created the per-app AI workflow configuration model and enforcement strategy:

- **AppAiWorkflowConfig** sealed record with: AppId, Mode (AiWorkflowMode enum), BudgetLimitPerMonth, BudgetLimitPerRequest, PreferredProvider, PreferredModel, RequireApproval, MaxConcurrentRequests, AllowedOperations, CreatedAt, UpdatedAt
- **AiWorkflowMode** enum with 4 values: Auto (Intelligence chooses), Manual (app specifies), Budget (cost-optimized), Approval (human gate for expensive ops)
- **AiUsageTracking** sealed record with: AppId, TotalSpentThisMonth, RequestCountThisMonth, ActiveConcurrentRequests, PeriodStart, LastRequestTime
- **AppAiWorkflowStrategy** (internal sealed) with:
  - ConfigureWorkflowAsync: stores config, initializes usage tracking, sends to Intelligence via `intelligence.workflow.configure`
  - RemoveWorkflowAsync: removes config/tracking, notifies Intelligence via `intelligence.workflow.remove`
  - GetWorkflowAsync/UpdateWorkflowAsync: CRUD operations with automatic UpdatedAt timestamps
  - ProcessAiRequestAsync: full enforcement pipeline (budget -> concurrency -> operation -> approval -> forward to `intelligence.request`)
  - ResetMonthlyUsageAsync/GetUsageAsync: usage management with live concurrent count merging
  - EnsureCurrentPeriod: automatic monthly reset when billing period rolls over
  - Thread-safe concurrent counting via `Interlocked.Increment`/`Interlocked.Decrement`

### Task 2: Wire AI workflow into AppPlatformPlugin with topic handlers
**Commit:** 0c38ba4

Integrated AI workflow into the plugin with 7 new message bus topic handlers:

- **PlatformTopics** updated with 7 constants: AiWorkflowConfigure, AiWorkflowRemove, AiWorkflowGet, AiWorkflowUpdate, AiRequest, AiUsageGet, AiUsageReset
- **AppPlatformPlugin** updated:
  - Added `_aiWorkflowStrategy` private field
  - Initialization in `InitializeServices()`: `new AppAiWorkflowStrategy(MessageBus!, Id)`
  - 7 subscriptions in `SubscribeToPlatformTopics()`
  - HandleAiWorkflowConfigureAsync: parses Mode enum, budget decimals, provider/model strings, approval bool, concurrency int, operations array
  - HandleAiWorkflowRemoveAsync: removes config by AppId
  - HandleAiWorkflowGetAsync: retrieves config by AppId
  - HandleAiWorkflowUpdateAsync: merges updates onto existing config
  - HandleAiRequestAsync: validates token, checks "intelligence" scope, delegates to strategy
  - HandleAiUsageGetAsync: returns current usage tracking
  - HandleAiUsageResetAsync: resets monthly counters
  - Added `ParseDecimal` helper for multi-type decimal extraction from payloads

## Verification Results

1. `dotnet build` -- zero errors, 44 warnings (all pre-existing SDK warnings)
2. AiWorkflowMode enum has all 4 modes: Auto, Manual, Budget, Approval
3. Budget enforcement checks both monthly (`TotalSpentThisMonth >= BudgetLimitPerMonth`) and per-request limits
4. Concurrency uses `Interlocked.Increment` and `Interlocked.Decrement` via `ConcurrentDictionary.AddOrUpdate`
5. AI requests validate token and check "intelligence" scope before processing
6. All 7 AI workflow topic handlers registered in plugin subscriptions
7. No direct plugin references -- all Intelligence communication via message bus topics

## Deviations from Plan

None -- plan executed exactly as written.

## Self-Check: PASSED

All files verified present, all commits verified in git history.
