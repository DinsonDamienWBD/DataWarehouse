# DataWarehouse.SDK Production Readiness Audit Report

**Date**: 2026-02-24
**Scope**: All 964 .cs files in `DataWarehouse.SDK/` (excluding `obj/` directories)
**Excluded (already audited)**: PluginBase.cs, StrategyBase.cs, IStrategy.cs, IPlugin.cs, ProviderInterfaces.cs, IntelligenceAwarePluginBase.cs, NewFeaturePluginBase.cs, DataPipelinePluginBase.cs, all files in `Contracts/Hierarchy/Feature/` and `Contracts/Hierarchy/DataPipeline/`
**Rules Applied**: Rule 13 (no stubs/placeholders/mocks), AD-05 (strategies are workers)

---

## Summary Statistics

| Metric | Count |
|--------|-------|
| Total source files scanned | 964 |
| Files excluded (already audited) | ~25 |
| Files with findings | ~45 |
| CRITICAL findings | 9 |
| HIGH findings | 11 |
| MEDIUM findings | 14 |
| LOW findings | 8 |
| INFO (false positives / acceptable) | ~60 |

---

## CRITICAL Findings (Rule 13 Violations - Entire Classes Are Stubs)

### C-01: Cloud Provider Simulations (3 files)
**Severity**: CRITICAL
**Files**:
- `Deployment/CloudProviders/AwsProvider.cs` (lines 35-76)
- `Deployment/CloudProviders/AzureProvider.cs` (lines 28-62)
- `Deployment/CloudProviders/GcpProvider.cs` (lines 28-62)

**Problem**: All three cloud providers are complete simulations. Every method returns fake data:
- `ProvisionVmAsync` returns `Task.FromResult($"i-{Guid.NewGuid():N}")` -- fake instance ID
- `ProvisionStorageAsync` returns fake volume IDs
- `DeprovisionAsync` always returns `true` without doing anything
- `GetMetricsAsync` returns hardcoded CPU/storage percentages (45%, 50%, 55%)
- `ListManagedResourcesAsync` returns empty arrays

**Fix**: These must either integrate with real cloud SDKs (AWSSDK.EC2, Azure.ResourceManager, Google.Cloud.Compute.V1) or be removed and replaced with a strategy pattern where plugins provide the real implementation.

---

### C-02: Edge Mesh Network Stubs (3 files)
**Severity**: CRITICAL
**Files**:
- `Edge/Mesh/LoRaMesh.cs` (entire file, 137 lines)
- `Edge/Mesh/ZigbeeMesh.cs` (entire file, 55 lines)
- `Edge/Mesh/BleMesh.cs` (entire file, 55 lines)

**Problem**: All three mesh implementations are self-described stubs. They:
- `SendMessageAsync` loops messages back to sender (simulates receipt)
- `DiscoverTopologyAsync` returns empty/single-node topology
- No actual hardware communication occurs
- Comments explicitly say "Stub: simulate message send", "Stub: loopback to simulate receipt"

**Fix**: Either implement real hardware SDK integration or remove these classes and leave only the `IMeshNetwork` interface for plugins to implement. The SDK should not ship simulation code.

---

### C-03: Linux MTD Flash Device Stub
**Severity**: CRITICAL
**File**: `Edge/Flash/FlashDevice.cs` (lines 76-138, `LinuxMtdFlashDevice` class)

**Problem**: Complete stub. Every method is a no-op:
- `EraseBlockAsync` returns `Task.CompletedTask` (no erase)
- `ReadPageAsync` zeroes the buffer (no read)
- `WritePageAsync` returns `Task.CompletedTask` (no write)
- `IsBlockBadAsync` always returns `false`
- `MarkBlockBadAsync` returns `Task.CompletedTask` (no mark)
- All comments say "Stub: MEMERASE ioctl", "Stub: pread", etc.

**Fix**: Implement via Linux P/Invoke to MTD ioctl calls, or remove and leave only the `IFlashDevice` interface.

---

### C-04: Routing Pipeline Stubs (2 classes)
**Severity**: CRITICAL
**File**: `Federation/Routing/RoutingPipeline.cs` (lines 56-117)

**Problem**: Both `ObjectPipeline` and `FilePathPipeline` return failure responses:
```csharp
return Task.FromResult(new StorageResponse
{
    Success = false,
    ErrorMessage = "ObjectPipeline not yet implemented (Phase 34-02)"
});
```
These are core routing components -- any request routed through them will fail.

**Fix**: Implement the routing logic or remove these classes and ensure the router does not attempt to use them.

---

### C-05: TPM2 Provider - All Operations Throw
**Severity**: CRITICAL
**File**: `Hardware/Accelerators/Tpm2Provider.cs` (lines 93-245)

**Problem**: Every cryptographic operation throws `InvalidOperationException` with "not yet implemented":
- `CreateKeyAsync` (line 114)
- `SignAsync` (line 149)
- `EncryptAsync` (line 182)
- `DecryptAsync` (line 216)
- `GetRandomAsync` (line 242)

The class detects TPM presence correctly but cannot use it. Every operation fails at runtime.

**Fix**: Integrate with TSS.MSR NuGet package for TPM2 command marshaling, or clearly mark as unavailable in capability registry so callers never attempt to use it.

---

### C-06: HSM Provider - All Operations Throw
**Severity**: CRITICAL
**File**: `Hardware/Accelerators/HsmProvider.cs` (lines 248-370+)

**Problem**: Same pattern as TPM2. Every operation throws:
- `GenerateKeyAsync` (line 267)
- `SignAsync` (line 301)
- `EncryptAsync` (line 332)
- `DecryptAsync` (line 365)

**Fix**: Integrate with PKCS#11 library, or mark as unavailable.

---

### C-07: QAT Accelerator - Encryption/Decryption Throw
**Severity**: CRITICAL
**File**: `Hardware/Accelerators/QatAccelerator.cs` (lines 376-401)

**Problem**: `EncryptQatAsync` and `DecryptQatAsync` throw `InvalidOperationException`:
```
"QAT encryption not yet implemented. Full QAT cryptographic API integration is deferred to future phases."
```
Compression/decompression works, but crypto operations fail at runtime.

**Fix**: Implement QAT crypto via CPA library, or remove the crypto methods from the interface.

---

## HIGH Findings (Significant Production Risks)

### H-01: AdaptiveIndex Factory Rejects Levels 3-6 Despite Having Implementations
**Severity**: HIGH
**Files**:
- `VirtualDiskEngine/AdaptiveIndex/AdaptiveIndexEngine.cs` (lines 336-339)
- `VirtualDiskEngine/AdaptiveIndex/MorphTransitionEngine.cs` (lines 535-538)

**Problem**: Both `CreateIndexForLevel` factories throw `NotSupportedException` for levels 3-6 (BeTree, LearnedIndex, BeTreeForest, DistributedRouting). However, full implementations exist:
- `BeTree.cs` (295 lines, fully implemented B-epsilon tree)
- `AlexLearnedIndex.cs` (283 lines, fully implemented)
- `BeTreeForest.cs` (301 lines, fully implemented)
- `DistributedRoutingIndex.cs` (326 lines, fully implemented)

These classes can never be instantiated through the normal factory path -- they are dead code.

**Fix**: Update the factory methods to instantiate these classes instead of throwing. The implementations exist and appear complete.

---

### H-02: VDE Indirect Block Support Missing
**Severity**: HIGH
**Files**:
- `VirtualDiskEngine/VirtualDiskEngine.cs` (lines 264, 382)
- `VirtualDiskEngine/Metadata/InodeTable.cs` (line 553)

**Problem**: Files exceeding the direct block count (typically 12 blocks) throw `NotSupportedException`. This limits file size to `12 * BlockSize` (e.g., 48KB with 4KB blocks). Any file larger than this limit will fail to write.

The Inode structure already HAS `IndirectBlockPointer` and `DoubleIndirectPointer` fields, but the allocation code never writes through them.

**Fix**: Implement indirect block allocation and traversal. This is a fundamental filesystem feature.

---

### H-03: DeveloperExperience GraphQL Returns Mock Data
**Severity**: HIGH
**File**: `Infrastructure/DeveloperExperience.cs` (lines 1297-1305)

**Problem**: The `FindResolver` method returns a "default resolver" that produces fake data for unregistered GraphQL fields:
```csharp
return new Dictionary<string, object>
{
    ["message"] = $"Resolver not implemented for {fieldName}"
};
```
This silently returns mock data instead of signaling an error.

**Fix**: Return `null` or throw `InvalidOperationException` so callers know the field is not resolvable.

---

### H-04: HypervisorDetector CPUID Returns Empty String
**Severity**: HIGH
**File**: `Hardware/Hypervisor/HypervisorDetector.cs` (lines 190-198)

**Problem**: `GetCpuidHypervisorSignature()` always returns `string.Empty`. The method has full documentation of how to implement it via `System.Runtime.Intrinsics.X86.X86Base.CpuId` but the actual code is a placeholder.

**Fix**: Implement using `System.Runtime.Intrinsics.X86.X86Base.CpuId(0x40000000, 0)` which is available in .NET 6+.

---

### H-05: LocationAwareReplicaSelector Always Returns Null Leader
**Severity**: HIGH
**File**: `Federation/Replication/LocationAwareReplicaSelector.cs` (lines 184-192)

**Problem**: `GetLeaderNodeIdAsync` always returns `null` because `IConsensusEngine` does not expose leader discovery. This means strong consistency reads can never be routed to the leader.

**Fix**: Extend `IConsensusEngine` with `GetLeaderNodeIdAsync()` and implement in the Raft engine.

---

### H-06: EdgeProfileEnforcer Plugin Filtering Is a No-Op
**Severity**: HIGH
**File**: `Deployment/EdgeProfiles/EdgeProfileEnforcer.cs` (lines 140-165)

**Problem**: The `FilterPlugins` method only logs a message. It does not actually disable any plugins. The comment says "This is a placeholder -- actual implementation would call KernelInfrastructure.DisablePluginsExcept".

**Fix**: Wire up to KernelInfrastructure to actually disable plugins.

---

### H-07: KernelInfrastructure Version Compatibility Always Returns True
**Severity**: HIGH
**File**: `Infrastructure/KernelInfrastructure.cs` (lines 474-484)

**Problem**: `CheckVersionCompatibilityAsync` always returns `IsCompatible = true`. Comment says "Placeholder for actual version checking logic". This means incompatible plugins will be loaded without warning.

**Fix**: Implement actual version compatibility checking (SdkCompatibility attribute, assembly version, interface hash).

---

### H-08: CoAP Observe Returns No-Op Disposable
**Severity**: HIGH
**File**: `Edge/Protocols/CoApClient.cs` (lines 131-139)

**Problem**: `ObserveAsync` returns a `NoOpDisposable` without registering any observation. Callers expecting to receive resource change notifications will get nothing.

**Fix**: Implement RFC 7641 observe pattern (send GET with Observe option, process notifications).

---

### H-09: BareMetalOptimizer SPDK Binding Is Simulated
**Severity**: HIGH
**File**: `Deployment/BareMetalOptimizer.cs` (lines 140-177)

**Problem**: After passing all safety checks, the SPDK binding is never actually performed. The code logs "SPDK user-space NVMe ACTIVE (simulated)" but never calls the Phase 35 NvmePassthroughStrategy. The delegation code is commented out (lines 146-153).

**Fix**: Wire up the actual SPDK binding via NvmePassthroughStrategy.

---

### H-10: BalloonDriver Memory Pressure Notification Is Placeholder
**Severity**: HIGH
**File**: `Hardware/Hypervisor/BalloonDriver.cs`

**Problem**: Per documentation, "Actual hypervisor-specific pressure notification (via vmballoon, hv_balloon, virtio-balloon) is marked as future work and currently uses simplified placeholders." The driver detects the hypervisor but cannot respond to memory pressure events.

**Fix**: Implement hypervisor-specific pressure notification for at least KVM (virtio-balloon) and Hyper-V (hv_balloon).

---

### H-11: CoAP Client Silently Swallows All Receive Errors
**Severity**: HIGH
**File**: `Edge/Protocols/CoApClient.cs` (lines 256-267)

**Problem**: The receive loop catches all exceptions silently:
```csharp
catch (Exception)
{
    // Ignore receive errors (network issues, cancellation, etc.)
}
```
Network errors, protocol violations, and deserialization failures are all silently discarded. Pending requests may time out without explanation.

**Fix**: Log errors at minimum. Distinguish between cancellation (expected) and real failures.

---

## MEDIUM Findings

### M-01: InMemory Infrastructure Classes with TODO for v6.0 (2 files)
**Severity**: MEDIUM
**Files**:
- `Infrastructure/InMemory/InMemoryP2PNetwork.cs` (line 22)
- `Infrastructure/InMemory/InMemoryClusterMembership.cs` (line 22)

**Problem**: Both contain `TODO (v6.0)` comments. They are single-node implementations which are production-ready for single-node but provide no distributed functionality.

**Fix**: Remove TODO comments. These are legitimate single-node implementations. The TODO should be tracked in a task tracker, not in code.

---

### M-02: NamespaceAuthority TODO Comment
**Severity**: MEDIUM
**File**: `VirtualDiskEngine/Identity/NamespaceAuthority.cs` (line 45)

**Problem**: Contains `// TODO: Replace with real Ed25519 when targeting .NET 9+ or adding a NuGet Ed25519 package`. The HMAC fallback is functional but not as secure as Ed25519.

**Fix**: Remove TODO comment. The HMAC-SHA512 implementation is a legitimate production fallback. Track Ed25519 upgrade separately.

---

### M-03: NvmePassthrough Returns Placeholder Completions
**Severity**: MEDIUM
**File**: `Hardware/NVMe/NvmePassthrough.cs` (line 48 documentation)

**Problem**: Documentation states "actual IOCTL/ioctl command marshaling is marked as future work and currently returns placeholder completions." However, reviewing the code (lines 249-350), the Windows and Linux implementations DO appear to use real IOCTL calls. The documentation may be outdated.

**Fix**: Verify the Windows/Linux implementations are complete and update the misleading documentation.

---

### M-04: VDE CopyOnWrite Indirect Block Traversal Incomplete
**Severity**: MEDIUM
**Files**:
- `VirtualDiskEngine/CopyOnWrite/SpaceReclaimer.cs` (lines 140-153)
- `VirtualDiskEngine/CopyOnWrite/SnapshotManager.cs` (lines 305-313)

**Problem**: When reclaiming space from snapshots, only the indirect/double-indirect pointer block itself is collected, not the data blocks it references. This could leak blocks.

**Fix**: Implement recursive block collection when indirect block pointers are present.

---

### M-05: QueryExecutionEngine External Sort Not Implemented
**Severity**: MEDIUM
**File**: `Contracts/Query/QueryExecutionEngine.cs` (line 797)

**Problem**: Comment says "Collect all batches into memory (external sort for > 512MB not implemented yet)". Large ORDER BY queries on datasets exceeding available memory will OOM.

**Fix**: Implement external sort (spill to disk) for large result sets, or enforce a configurable memory limit.

---

### M-06: OnlineDefragmenter Uses Placeholder Source Blocks
**Severity**: MEDIUM
**File**: `VirtualDiskEngine/Maintenance/OnlineDefragmenter.cs` (lines 525-531)

**Problem**: The defragmentation candidate generator uses synthetic block addresses (`baseBlock + j * 2`) instead of real inode extent entries.

**Fix**: Connect to actual inode extent tree to get real fragmented block addresses.

---

### M-07: BackgroundInodeMigration Placeholder Target Module
**Severity**: MEDIUM
**File**: `VirtualDiskEngine/ModuleManagement/BackgroundInodeMigration.cs` (line 384)

**Problem**: `TargetModule = ModuleId.Security` is hardcoded as a placeholder with comment "actual module in manifest diff".

**Fix**: Derive the target module from the actual manifest diff comparison.

---

### M-08: ExtendibleHashTable Placeholder Directory Entries
**Severity**: MEDIUM
**File**: `VirtualDiskEngine/AdaptiveIndex/ExtendibleHashTable.cs` (line 105)

**Problem**: Directory entries initialized with negative placeholder values: `_directory[i] = -(i + 1)`.

**Fix**: Verify this is intentional (lazy bucket creation) and document clearly, or initialize properly.

---

### M-09: TagIndexRegion Placeholder NextLeaf
**Severity**: MEDIUM
**File**: `VirtualDiskEngine/Regions/TagIndexRegion.cs` (line 643)

**Problem**: `node.NextLeaf = null; // placeholder` during B+ tree node split. Missing sibling link could break range scans.

**Fix**: Set NextLeaf to the correct sibling node after split.

---

### M-10: DataTransitStrategyBase Resume Throws
**Severity**: MEDIUM
**File**: `Contracts/Transit/DataTransitStrategyBase.cs` (lines 110-113)

**Problem**: Base class `ResumeTransferAsync` throws two different exceptions:
- `NotSupportedException` if strategy doesn't support resumable transfers
- `NotSupportedException` if "strategy has not implemented resume logic"

The second throw (line 113) is a "not implemented" disguised as "not supported".

**Fix**: Strategies that claim to support resume must implement it. Add an abstract method or a capability check.

---

### M-11: HostedOptimizer "Not implemented in this version"
**Severity**: MEDIUM
**File**: `Deployment/HostedOptimizer.cs` (line 202)

**Problem**: Returns an error message containing "Not implemented in this version" for a filesystem optimization path.

**Fix**: Either implement the optimization or remove the code path entirely with a clear capability check.

---

### M-12: Multiple Silently Swallowed Exceptions in catch (Exception)
**Severity**: MEDIUM
**Files** (selected -- there are ~30 total, most are acceptable patterns):
- `Edge/Protocols/MqttClient.cs:359` -- MQTT receive errors swallowed
- `VirtualDiskEngine/Cache/ArcCacheL3NVMe.cs:339` -- NVMe cache errors swallowed
- `VirtualDiskEngine/Cache/ArcCacheL2Mmap.cs:92,159` -- mmap errors swallowed
- `Storage/Placement/AutonomousRebalancer.cs:290,338` -- rebalance errors swallowed

**Problem**: Exceptions caught and silently discarded without logging. While some are intentional graceful degradation, they should at minimum log at Debug/Trace level.

**Fix**: Add logging to all catch blocks that swallow exceptions.

---

### M-13: Ecosystem Connection Pool SemaphoreFullException Swallowed
**Severity**: MEDIUM
**File**: `Contracts/Ecosystem/ConnectionPoolImplementations.cs` (lines 345, 348)

**Problem**: `SemaphoreFullException` caught with empty body. While this is a known pattern for semaphore over-release prevention, it masks programming errors.

**Fix**: Log at Debug level when this occurs as it may indicate a double-release bug.

---

### M-14: ProximityCalculator CostOptimized Not Implemented
**Severity**: MEDIUM
**File**: `Federation/Topology/ProximityCalculator.cs` (line 57)

**Problem**: Comment says "CostOptimized: not yet implemented (future: prefer on-prem or cheap regions)".

**Fix**: Implement or remove the option from the enum so callers cannot select it.

---

## LOW Findings

### L-01: Intelligence "Stub Types" Naming
**Severity**: LOW (naming only -- types are properly implemented)
**Files**:
- `Contracts/TransitEncryptionPluginBases.cs:1061` -- "#region Stub Types for Transit Encryption Intelligence Integration"
- `Contracts/HardwareAccelerationPluginBases.cs:1050` -- "#region Intelligence Stub Types"
- `Contracts/TamperProof/IWormStorageProvider.cs:537`
- `Contracts/TamperProof/ITimeLockProvider.cs:409`
- `Contracts/TamperProof/ITamperProofProvider.cs:810`
- `Contracts/TamperProof/IIntegrityProvider.cs:414`
- `Contracts/TamperProof/IBlockchainProvider.cs:742`
- `Contracts/TamperProof/IAccessLogProvider.cs:637`
- `Contracts/MilitarySecurityPluginBases.cs:748`
- `Contracts/LowLatencyPluginBases.cs:572`

**Problem**: These are fully-defined record types (data carriers) mislabeled as "Stub Types". They are not stubs -- they are proper DTOs for AI intelligence integration.

**Fix**: Rename regions from "Stub Types" to "Intelligence Integration Types" or "AI Integration DTOs".

---

### L-02: AuthorityChainFacade "Standard Member Placeholders"
**Severity**: LOW
**File**: `Infrastructure/Authority/AuthorityChainFacade.cs` (line 257)

**Problem**: Default quorum config comment says "3-of-5 with standard member placeholders". The actual values should be configurable.

**Fix**: Ensure quorum members are populated from real configuration, not hardcoded.

---

### L-03: PolicySimulationSandbox Placeholder Severity
**Severity**: LOW
**File**: `Infrastructure/Policy/Performance/PolicySimulationSandbox.cs` (line 127)

**Problem**: `Severity = ImpactSeverity.None // Placeholder; classified below`. The severity is correctly set later in the flow.

**Fix**: Remove misleading comment or initialize to a sentinel value.

---

### L-04: PinMapping Partial Placeholder Mappings
**Severity**: LOW
**File**: `Edge/Bus/PinMapping.cs` (lines 27-28)

**Problem**: Comments note "partial mapping, placeholder for future expansion" for BeagleBone Black and Jetson Nano.

**Fix**: Complete the pin mappings or clearly document which pins are mapped and which are not.

---

### L-05: ExtendibleHashTable Deferred Bucket Merging
**Severity**: LOW
**File**: `VirtualDiskEngine/AdaptiveIndex/ExtendibleHashTable.cs` (line 300)

**Problem**: "Bucket merging with siblings is deferred (not implemented in this version)." This means the hash table can only grow, never shrink.

**Fix**: Implement bucket merging for memory efficiency, or document the limitation.

---

### L-06: ArtNode FindChild Returns Null
**Severity**: LOW
**File**: `VirtualDiskEngine/AdaptiveIndex/ArtNode.cs` (line 117)

**Problem**: `public override ArtNode? FindChild(byte keyByte) => null;` in a leaf node. This is correct behavior (leaf nodes have no children).

**Fix**: None needed. This is correct.

---

### L-07: ISecurityContext Default Interface Method
**Severity**: LOW
**File**: `Security/ISecurityContext.cs` (line 33)

**Problem**: `CommandIdentity? CommandIdentity => null; // Default interface method for backward compatibility`. Default returns null for backward compatibility.

**Fix**: None needed. This is a proper default interface method pattern.

---

### L-08: GetDefaultStrategyId Returns Null
**Severity**: LOW
**Files**: Multiple PluginBase subclasses (StreamingPluginBase, SecurityPluginBase, ResiliencePluginBase, ObservabilityPluginBase, StoragePluginBase)

**Problem**: `protected override string? GetDefaultStrategyId() => null;` -- returns null meaning "no default strategy".

**Fix**: None needed. This is an intentional design choice -- plugins must explicitly configure their strategy.

---

## INFO / False Positives (Acceptable Patterns)

The following were flagged by scanning but are NOT issues:

1. **NotSupportedException in Stream adapters** (`IO/PushToPullStreamAdapter.cs:49-53`): Read-only stream correctly rejects Write/SetLength/Seek. Standard .NET Stream pattern.

2. **NotSupportedException in format validation** (`Contracts/ActiveStoragePluginBases.cs:2015,3135,3140`): Rejects unsupported input/output formats with descriptive messages. Correct behavior.

3. **NotSupportedException in switch expressions** (~40 occurrences): Used as exhaustive pattern match catch-all for unknown enum values, unsupported operations, etc. These are standard error handling.

4. **PlatformNotSupportedException** (`Hardware/NVMe/NvmePassthrough.cs:222,495`): Thrown on non-Windows/non-Linux platforms. Correct -- NVMe passthrough is OS-specific.

5. **NotSupportedException in capability guards** (`Contracts/Streaming/StreamingStrategy.cs:696-714`, `Contracts/Observability/ObservabilityStrategyBase.cs:64-90`): Guard methods that check capabilities before operations. If capability is not supported, throwing is correct.

6. **NullMessageBus / NullLoggerProvider** (`Contracts/NullObjects.cs`): Proper null-object pattern. Not stubs.

7. **Task.CompletedTask in virtual hook methods** (~274 occurrences): Intentional override hooks. Acceptable per audit rules.

8. **SemaphoreFullException empty catch** (`IO/DeterministicIo/DeadlineScheduler.cs:201`): Standard semaphore over-release guard.

9. **TODO in generated code** (`Contracts/Ecosystem/TerraformProviderSpecification.cs:559-610`, `Contracts/Ecosystem/SdkLanguageTemplates.cs:410-942`): TODOs appear in generated code templates (Go, Java, Rust, JS output), not in SDK logic itself.

10. **InMemoryCircuitBreaker** (`Infrastructure/InMemory/InMemoryCircuitBreaker.cs`): Comment explicitly says "This is production-ready for single-node deployments -- not a stub." Reviewed and confirmed.

11. **catch (Exception) with re-throw** (`Edge/Flash/FlashTranslationLayer.cs:177`): Catches, marks block bad, then re-throws. Correct pattern.

12. **catch (Exception) with fallback** (`Contracts/Persistence/DefaultPluginStateStore.cs:122-292`): Falls back to backing store on primary failure. Correct resilience pattern.

---

## Systemic Observations

1. **Hardware provider pattern**: TPM2, HSM, QAT all follow the same anti-pattern: detect hardware correctly, register capabilities, but throw on every operation. This is 3 classes with the same issue. The capability registry reports them as available, but they cannot be used.

2. **Edge/IoT hardware stubs**: The Edge subsystem (mesh networks, flash devices) contains classes that are explicitly labeled as stubs. These should either be fully implemented or removed from the SDK, leaving only interfaces.

3. **VDE indirect blocks**: Multiple components (VirtualDiskEngine, InodeTable, SpaceReclaimer, SnapshotManager) reference indirect block support but none implement it. This is a systemic gap that limits file size.

4. **AdaptiveIndex levels 3-6**: Four fully-implemented index classes (BeTree, AlexLearnedIndex, BeTreeForest, DistributedRoutingIndex) exist but are unreachable through the factory. This is likely a configuration oversight from when the factories were written before the implementations were complete.

5. **"Not yet implemented" vs NotSupportedException**: The codebase uses NotSupportedException for both "this feature is not supported by design" and "this feature is not yet implemented." These should be distinguished -- the former is correct, the latter violates Rule 13.

---

## Priority Remediation Order

1. **Phase 1 (Immediate)**: Fix H-01 (AdaptiveIndex factory) -- implementations exist, just wire them up
2. **Phase 2 (High Priority)**: Fix C-01 through C-07 -- remove or replace stubs with real implementations
3. **Phase 3 (Important)**: Fix H-02 through H-11 -- production risks
4. **Phase 4 (Quality)**: Fix M-01 through M-14 -- medium issues
5. **Phase 5 (Polish)**: Fix L-01 through L-08 -- naming and documentation
