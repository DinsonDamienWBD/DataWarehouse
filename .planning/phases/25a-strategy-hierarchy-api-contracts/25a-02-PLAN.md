---
phase: 25a-strategy-hierarchy-api-contracts
plan: 02
type: execute
wave: 2
depends_on: ["25a-01"]
files_modified:
  - DataWarehouse.SDK/Contracts/Encryption/EncryptionStrategy.cs
  - DataWarehouse.SDK/Contracts/Compression/CompressionStrategy.cs
  - DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs
  - DataWarehouse.SDK/Contracts/Security/SecurityStrategy.cs
  - DataWarehouse.SDK/Contracts/Compliance/ComplianceStrategy.cs
  - DataWarehouse.SDK/Contracts/Streaming/StreamingStrategy.cs
  - DataWarehouse.SDK/Contracts/Replication/ReplicationStrategy.cs
  - DataWarehouse.SDK/Contracts/Transit/DataTransitStrategyBase.cs
  - DataWarehouse.SDK/Contracts/Interface/InterfaceStrategyBase.cs
  - DataWarehouse.SDK/Contracts/Media/MediaStrategyBase.cs
  - DataWarehouse.SDK/Contracts/Observability/ObservabilityStrategyBase.cs
  - DataWarehouse.SDK/Contracts/Compute/PipelineComputeStrategy.cs
  - DataWarehouse.SDK/Contracts/DataFormat/DataFormatStrategy.cs
  - DataWarehouse.SDK/Contracts/DataLake/DataLakeStrategy.cs
  - DataWarehouse.SDK/Contracts/DataMesh/DataMeshStrategy.cs
  - DataWarehouse.SDK/Contracts/StorageProcessing/StorageProcessingStrategy.cs
  - DataWarehouse.SDK/Contracts/RAID/RaidStrategy.cs
  - DataWarehouse.SDK/Connectors/ConnectionStrategyBase.cs
  - DataWarehouse.SDK/Security/IKeyStore.cs
autonomous: true

must_haves:
  truths:
    - "All 17+ domain strategy bases inherit from StrategyBase (directly or through their domain interface chain)"
    - "Intelligence Integration regions (#region Intelligence Integration) are removed from all 17+ bases"
    - "ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus property, IsIntelligenceAvailable removed from all bases"
    - "All domain-specific contracts (EncryptAsync/DecryptAsync, StoreAsync/RetrieveAsync, etc.) are preserved exactly as-is"
    - "All 17+ modified files compile with zero errors under TreatWarningsAsErrors"
  artifacts:
    - path: "DataWarehouse.SDK/Contracts/Encryption/EncryptionStrategy.cs"
      provides: "EncryptionStrategyBase inheriting StrategyBase with Encrypt/Decrypt contract"
      contains: "class EncryptionStrategyBase : StrategyBase"
    - path: "DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs"
      provides: "StorageStrategyBase inheriting StrategyBase with Store/Retrieve/Delete/List contract"
      contains: "class StorageStrategyBase : StrategyBase"
    - path: "DataWarehouse.SDK/Contracts/Compression/CompressionStrategy.cs"
      provides: "CompressionStrategyBase inheriting StrategyBase with Compress/Decompress contract"
      contains: "class CompressionStrategyBase : StrategyBase"
    - path: "DataWarehouse.SDK/Contracts/Security/SecurityStrategy.cs"
      provides: "SecurityStrategyBase inheriting StrategyBase"
      contains: "class SecurityStrategyBase : StrategyBase"
    - path: "DataWarehouse.SDK/Contracts/Compliance/ComplianceStrategy.cs"
      provides: "ComplianceStrategyBase inheriting StrategyBase"
      contains: "class ComplianceStrategyBase : StrategyBase"
    - path: "DataWarehouse.SDK/Connectors/ConnectionStrategyBase.cs"
      provides: "ConnectionStrategyBase inheriting StrategyBase"
      contains: "class ConnectionStrategyBase : StrategyBase"
    - path: "DataWarehouse.SDK/Security/IKeyStore.cs"
      provides: "KeyStoreStrategyBase inheriting StrategyBase"
      contains: "class KeyStoreStrategyBase : StrategyBase"
  key_links:
    - from: "DataWarehouse.SDK/Contracts/Encryption/EncryptionStrategy.cs"
      to: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      via: "class inheritance"
      pattern: "class EncryptionStrategyBase : StrategyBase"
    - from: "DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs"
      to: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      via: "class inheritance"
      pattern: "class StorageStrategyBase : StrategyBase"
    - from: "DataWarehouse.SDK/Connectors/ConnectionStrategyBase.cs"
      to: "DataWarehouse.SDK/Contracts/StrategyBase.cs"
      via: "class inheritance"
      pattern: "class ConnectionStrategyBase : StrategyBase"
---

<objective>
Refactor all 17+ existing domain strategy bases to inherit from the new StrategyBase and remove all intelligence integration code (STRAT-02, STRAT-05).

Purpose: Consolidate the fragmented strategy hierarchy under a single root. Remove ~1,500-2,000 lines of duplicated intelligence boilerplate (ConfigureIntelligence, GetStrategyKnowledge, GetStrategyCapability, MessageBus, IsIntelligenceAvailable) per AD-05. Preserve ALL domain-specific contracts exactly as-is.

Output: All 17+ strategy base files modified to inherit StrategyBase with intelligence code removed.
</objective>

<execution_context>
@C:/Users/ddamien/.claude/get-shit-done/workflows/execute-plan.md
@C:/Users/ddamien/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/ARCHITECTURE_DECISIONS.md (AD-05, AD-08)
@.planning/REQUIREMENTS.md (STRAT-02, STRAT-05)
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-RESEARCH.md (Section 1-3: hierarchy map, duplicated patterns, domain distribution)
@.planning/phases/25a-strategy-hierarchy-api-contracts/25a-01-SUMMARY.md
@DataWarehouse.SDK/Contracts/StrategyBase.cs (from Plan 01)
@DataWarehouse.SDK/Contracts/IStrategy.cs (from Plan 01)
</context>

<tasks>

<task type="auto">
  <name>Task 1: Refactor the 7 original domain bases to inherit StrategyBase and remove intelligence</name>
  <files>
    DataWarehouse.SDK/Contracts/Encryption/EncryptionStrategy.cs
    DataWarehouse.SDK/Contracts/Compression/CompressionStrategy.cs
    DataWarehouse.SDK/Contracts/Storage/StorageStrategy.cs
    DataWarehouse.SDK/Contracts/Security/SecurityStrategy.cs
    DataWarehouse.SDK/Contracts/Compliance/ComplianceStrategy.cs
    DataWarehouse.SDK/Contracts/Streaming/StreamingStrategy.cs
    DataWarehouse.SDK/Contracts/Replication/ReplicationStrategy.cs
  </files>
  <action>
For each of the 7 original domain strategy base files, apply these mechanical transformations:

**Step A: Add `: StrategyBase` to the class declaration.**

Each file contains an abstract class that currently implements its domain interface directly. Change the class declaration to inherit from StrategyBase AND keep its domain interface. Examples:

- `EncryptionStrategyBase : IEncryptionStrategy` --> `EncryptionStrategyBase : StrategyBase, IEncryptionStrategy`
- `StorageStrategyBase : IStorageStrategy` --> `StorageStrategyBase : StrategyBase, IStorageStrategy`
- `CompressionStrategyBase : ICompressionStrategy` --> `CompressionStrategyBase : StrategyBase, ICompressionStrategy`
- `ComplianceStrategyBase : IComplianceStrategy` --> `ComplianceStrategyBase : StrategyBase, IComplianceStrategy`
- `StreamingStrategyBase : IStreamingStrategy` --> `StreamingStrategyBase : StrategyBase, IStreamingStrategy`
- `ReplicationStrategyBase : IReplicationStrategy` --> `ReplicationStrategyBase : StrategyBase, IReplicationStrategy`
- `SecurityStrategyBase : ISecurityStrategy` --> `SecurityStrategyBase : StrategyBase, ISecurityStrategy`

**Step B: Remove the Intelligence Integration region entirely.**

In each file, find and DELETE the entire `#region Intelligence Integration` ... `#endregion` block. This removes:
- `protected IMessageBus? MessageBus { get; private set; }` property
- `public virtual void ConfigureIntelligence(IMessageBus? messageBus)` method
- `protected bool IsIntelligenceAvailable => MessageBus != null;` property
- `public virtual KnowledgeObject GetStrategyKnowledge()` method
- `public virtual RegisteredCapability GetStrategyCapability()` method
- Any intelligence helper methods (e.g., `RequestXxxOptimizationAsync`)

Also remove the `using DataWarehouse.SDK.AI;` import if it is ONLY used by intelligence code. Check whether KnowledgeObject, RegisteredCapability, CapabilityCategory, IMessageBus are used elsewhere in the file before removing the using.

**Step C: Handle StrategyId/Name conflicts.**

StrategyBase declares `abstract string StrategyId` and `abstract string Name`. These already exist as abstract properties in most domain bases. When the base inherits from StrategyBase, the existing abstract declarations satisfy the base class requirement. If the existing declarations have `override` instead of `abstract`, they remain correct. If there's a conflict (both StrategyBase and the domain interface declare `Name`/`StrategyId`), ensure the domain base uses `override` for StrategyBase's abstract and explicit interface implementation if the interface also requires it. Most likely, the existing `abstract string StrategyId { get; }` and `abstract string Name { get; }` can simply be changed to `public override abstract string StrategyId { get; }` (if they were already public abstract, the `override` keyword gets added).

NOTE: If the base already has `public abstract string StrategyId { get; }` and StrategyBase also has it as abstract, they naturally merge via inheritance. No change needed unless the compiler complains. Let the compiler guide you -- build after each file.

**Step D: Handle IDisposable conflicts.**

StrategyBase implements IDisposable + IAsyncDisposable. Some domain bases (InterfaceStrategyBase, ObservabilityStrategyBase) also implement IDisposable directly. For THIS task (the 7 original bases), check:
- If the base already has `Dispose(bool)` pattern, convert it to `override` the StrategyBase version
- If the base does NOT have dispose, no action needed (inherited from StrategyBase)
- Remove `IDisposable` from the class declaration if StrategyBase already provides it
- Keep any domain-specific dispose logic (e.g., closing connections, releasing hardware) in the overridden `Dispose(bool)` calling `base.Dispose(disposing)`

**Step E: Preserve EVERYTHING else.**

Do NOT modify:
- Domain-specific abstract methods (EncryptAsync, DecryptAsync, StoreAsync, etc.)
- Domain-specific types (SecurityLevel, CipherCapabilities, StorageCapabilities, etc.)
- Statistics tracking code
- Retry logic
- Template method patterns (XxxCore methods)
- Any domain-specific helper methods

**Per-file specifics:**

1. **EncryptionStrategy.cs** (~1,466 lines): Large file with types + base class + interface. Keep all types (SecurityLevel, CipherCapabilities, CipherInfo, etc.). The intelligence block is ~150 lines. Remove it. Keep the IKeyStore integration code (that's domain logic, not intelligence).

2. **StorageStrategy.cs** (~923 lines): Contains IStorageStrategy interface, StorageStrategyBase class, and types. Keep all types (StorageCapabilities, StorageObjectMetadata, StorageHealthInfo, etc.). Keep the retry logic and health caching.

3. **CompressionStrategy.cs** (~1,144 lines): Contains types + ICompressionStrategy + CompressionStrategyBase. Keep all compression types and the content detection, entropy calculation logic.

4. **ComplianceStrategy.cs** (~1,125 lines): Contains types + IComplianceStrategy + ComplianceStrategyBase. Keep all compliance types and violation tracking.

5. **StreamingStrategy.cs** (~927 lines): Contains types + IStreamingStrategy + StreamingStrategyBase. Keep all streaming types and stream management.

6. **ReplicationStrategy.cs** (~628 lines): Contains types + IReplicationStrategy + ReplicationStrategyBase. Keep vector clock and conflict detection.

7. **SecurityStrategy.cs** (~1,559 lines): Contains types + ISecurityStrategy + SecurityStrategyBase. Keep Zero Trust and threat detection logic.

Build after EACH file: `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj`. Fix any compiler errors before moving to the next file. Common issues:
- `CS0114`: Missing `override` keyword (add it)
- `CS0108`: Hides inherited member (add `override` or `new` as appropriate)
- `CS0534`: Missing abstract member implementation (the abstract declaration in domain base satisfies it -- ensure it's marked correctly)
  </action>
  <verify>
Run `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- zero errors, zero warnings. Grep all 7 files for `ConfigureIntelligence` -- must return zero matches. Grep all 7 files for `: StrategyBase` -- must return exactly 7 matches (one per file).
  </verify>
  <done>All 7 original domain strategy bases inherit from StrategyBase, all intelligence integration regions removed, all domain-specific contracts preserved, SDK compiles with zero warnings.</done>
</task>

<task type="auto">
  <name>Task 2: Refactor remaining 10+ domain bases to inherit StrategyBase and remove intelligence</name>
  <files>
    DataWarehouse.SDK/Contracts/Transit/DataTransitStrategyBase.cs
    DataWarehouse.SDK/Contracts/Interface/InterfaceStrategyBase.cs
    DataWarehouse.SDK/Contracts/Media/MediaStrategyBase.cs
    DataWarehouse.SDK/Contracts/Observability/ObservabilityStrategyBase.cs
    DataWarehouse.SDK/Contracts/Compute/PipelineComputeStrategy.cs
    DataWarehouse.SDK/Contracts/DataFormat/DataFormatStrategy.cs
    DataWarehouse.SDK/Contracts/DataLake/DataLakeStrategy.cs
    DataWarehouse.SDK/Contracts/DataMesh/DataMeshStrategy.cs
    DataWarehouse.SDK/Contracts/StorageProcessing/StorageProcessingStrategy.cs
    DataWarehouse.SDK/Contracts/RAID/RaidStrategy.cs
    DataWarehouse.SDK/Connectors/ConnectionStrategyBase.cs
    DataWarehouse.SDK/Security/IKeyStore.cs
  </files>
  <action>
Apply the SAME mechanical transformations as Task 1 to the remaining 10+ domain strategy base files. Same steps A through E.

**Per-file specifics:**

1. **DataTransitStrategyBase.cs** (348 lines): Has statistics tracking (TransferCount, BytesTransferred, etc.) and transfer ID generation. KEEP all statistics and transfer management code. Remove intelligence region (~130 lines: lines 218-346). The intelligence block is particularly large here because it uses template methods (GetKnowledgeTopic, GetCapabilityCategory, GetStrategyDescription, GetKnowledgePayload, GetKnowledgeTags, GetCapabilityMetadata, GetSemanticDescription). Remove ALL of these intelligence helper methods.

2. **InterfaceStrategyBase.cs** (236 lines): Has its own IDisposable + lifecycle (Start/Stop). When inheriting StrategyBase:
   - Remove `IDisposable` from class declaration (inherited from StrategyBase)
   - Convert existing `Dispose(bool)` to `protected override void Dispose(bool disposing)` calling `base.Dispose(disposing)`
   - Keep the `_isRunning` state, `StartAsync`/`StopAsync` lifecycle (this is domain-specific lifecycle SEPARATE from StrategyBase's InitializeAsync/ShutdownAsync)
   - Keep `HandleRequestAsync` and all abstract Core methods
   - Remove intelligence region (lines 133-200)

3. **MediaStrategyBase.cs** (280 lines): Remove intelligence region. Keep transcode/extract contract.

4. **ObservabilityStrategyBase.cs** (298 lines): Has its own IDisposable + SemaphoreSlim initialization. When inheriting StrategyBase:
   - Remove `IDisposable` from interface list (inherited from StrategyBase)
   - Convert `Dispose(bool)` to `protected override void Dispose(bool disposing)` calling `base.Dispose(disposing)` -- keep `_initializationLock.Dispose()` in the override
   - Keep initialization lock pattern and all metrics/tracing/logging methods
   - Remove intelligence region (lines 33-122)

5. **PipelineComputeStrategy.cs** (661 lines): Remove intelligence region. Keep compute/sandbox contract.

6. **DataFormatStrategy.cs** (634 lines): Remove intelligence region. Keep serialize/deserialize contract.

7. **DataLakeStrategy.cs** (126 lines): Small file. Remove intelligence region. Keep lake-specific contract.

8. **DataMeshStrategy.cs** (109 lines): Small file. Remove intelligence region. Keep mesh-specific contract.

9. **StorageProcessingStrategy.cs** (535 lines): Remove intelligence region. Keep processing contract.

10. **RaidStrategy.cs** (647 lines): Remove intelligence region. Keep RAID-specific contract.

11. **ConnectionStrategyBase.cs** (626 lines): In `DataWarehouse.SDK/Connectors/` (different directory). Has ILogger support. When inheriting StrategyBase:
    - Change: `class ConnectionStrategyBase : IConnectionStrategy` --> `class ConnectionStrategyBase : StrategyBase, IConnectionStrategy`
    - Keep `ILogger` field and constructor parameter (domain-specific)
    - Keep retry logic, connection metrics, IntelligenceContext support for connection operations (this is NOT intelligence integration -- it's AI-suggested connection params passed as options by the plugin)
    - Remove intelligence region (lines 33-97): MessageBus, ConfigureIntelligence, IsIntelligenceAvailable, GetStrategyKnowledge, GetStrategyCapability
    - NOTE: ConnectionStrategyBase has `using DataWarehouse.SDK.Contracts.IntelligenceAware;` -- check if IntelligenceContext is used outside the intelligence region. If it is (e.g., for AI-suggested connection parameters), KEEP that using and the IntelligenceContext usage. Only remove ConfigureIntelligence/GetStrategyKnowledge/GetStrategyCapability/MessageBus/IsIntelligenceAvailable.

12. **IKeyStore.cs** (927 lines): In `DataWarehouse.SDK/Security/`. Contains KeyStoreStrategyBase class. Apply same changes. Keep all key management domain contract.

Also check for the **CategoryStrategyBases.cs** file in `DataWarehouse.SDK/Connectors/` which contains ~10 sub-category bases (DatabaseConnectionStrategyBase, MessagingConnectionStrategyBase, etc.). These inherit from ConnectionStrategyBase, NOT directly from StrategyBase. They should NOT need modification (they inherit StrategyBase transitively). But verify they compile.

Also check **StorageOrchestratorBase.cs** in `DataWarehouse.SDK/Contracts/` which research indicates has a DUPLICATE StorageStrategyBase. Read the file -- if it has a second StorageStrategyBase class, determine if it's the same class or different. If different, it also needs `: StrategyBase`. If same, it should not exist (flag for cleanup but don't delete -- AD-08 zero regression).

Build after EACH file: `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj`. Fix any compiler errors before proceeding.
  </action>
  <verify>
Run `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` -- zero errors, zero warnings. Grep ALL strategy base files across the SDK for `ConfigureIntelligence` -- must return zero matches. Grep ALL strategy base files for `: StrategyBase` or inherits chain through StrategyBase -- all 17+ bases should trace back to StrategyBase. Count of intelligence-removed lines should be 1,500-2,000+.
  </verify>
  <done>All 17+ domain strategy bases inherit from StrategyBase (directly or transitively), all intelligence integration code removed, all domain-specific contracts preserved, ConnectionStrategyBase sub-category bases compile, SDK builds with zero warnings.</done>
</task>

</tasks>

<verification>
1. `dotnet build DataWarehouse.SDK/DataWarehouse.SDK.csproj` exits with code 0
2. Grep for `ConfigureIntelligence` across all `.cs` files in DataWarehouse.SDK/Contracts/ and DataWarehouse.SDK/Connectors/ and DataWarehouse.SDK/Security/ -- zero matches in strategy base files
3. Grep for `GetStrategyKnowledge` across same directories -- zero matches in strategy base files
4. Grep for `GetStrategyCapability` across same directories -- zero matches in strategy base files
5. Grep for `protected IMessageBus? MessageBus` across same directories -- zero matches in strategy base files
6. Grep for `: StrategyBase` -- at least 17 matches across domain base files
7. All domain-specific interface implementations preserved (IEncryptionStrategy, IStorageStrategy, etc.)
</verification>

<success_criteria>
- All 17+ domain strategy bases inherit from StrategyBase
- ~1,500-2,000 lines of intelligence boilerplate removed
- All domain-specific contracts (Encrypt/Decrypt, Store/Retrieve/Delete/List, Compress/Decompress, etc.) preserved exactly
- Dispose patterns properly delegated to StrategyBase via override
- SDK builds with zero errors and zero warnings
- No domain-specific logic lost (AD-08 Zero Regression)
</success_criteria>

<output>
After completion, create `.planning/phases/25a-strategy-hierarchy-api-contracts/25a-02-SUMMARY.md`
</output>
