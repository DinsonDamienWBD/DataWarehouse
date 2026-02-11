# Phase 18: Plugin Deprecation & File Cleanup - Research

**Researched:** 2026-02-11
**Domain:** .NET solution file management, bulk project removal, C# codebase cleanup
**Confidence:** HIGH

## Summary

Phase 18 is a cleanup phase focused on removing legacy plugins that have been fully absorbed into Ultimate/Universal plugins via the strategy pattern. The existing DEPRECATION-ANALYSIS.md (from Phase 14-05) provides a strong foundation but overcounts the work: it claims 127+ plugins when only **88 deprecated plugins actually remain on disk**. The remaining ~39 were already removed in previous phases (Compression, Encryption, KeyManagement, etc.).

The verified current state is: 145 plugin directories on disk, of which 88 are deprecated and must be deleted. Of those 88, 66 still have entries in the DataWarehouse.slnx solution file and 22 have already been removed from slnx but their directories remain. No external code references deprecated plugins (all self-referencing within their own directories). The test project already excludes deprecated-plugin test files via `<Compile Remove>` entries.

**Primary recommendation:** Execute in three phases: (1) verify/identify with the corrected 88-plugin list, (2) remove 66 entries from slnx and delete all 88 directories, (3) clean up orphaned test files and documentation references, then verify build.

## Standard Stack

### Core
| Tool | Purpose | Why Standard |
|------|---------|--------------|
| DataWarehouse.slnx | XML-based solution file (.NET 10) | The slnx format is a simple XML file with `<Project>` entries; editable with text tools or `dotnet sln` |
| `dotnet build` | Build verification | Standard .NET CLI for verifying clean build post-removal |
| `dotnet clean` | Clear build artifacts | Ensures stale obj/bin from deleted projects do not interfere |
| PowerShell/Bash | Bulk directory deletion | Standard tooling for removing 88 directories (3,673 files) |

### Supporting
| Tool | Purpose | When to Use |
|------|---------|-------------|
| `git rm -r` | Track deletions in git | Use instead of raw `rm -rf` so git tracks the removal |
| `grep -r` / `rg` | Reference scanning | Verify no remaining references post-deletion |

## Architecture Patterns

### slnx File Structure

The DataWarehouse.slnx uses the modern .NET slnx XML format:

```xml
<Solution>
  <Folder Name="/Plugins/">
    <File Path="Plugin Readme.txt" />
    <Project Path="Plugins/DataWarehouse.Plugins.Name/DataWarehouse.Plugins.Name.csproj" />
    <!-- More projects... -->
  </Folder>
</Solution>
```

Each deprecated plugin is a single `<Project>` line in the `/Plugins/` folder. The WinFspDriver has an additional `<Platform>` child element but is a KEEP plugin.

### Plugin Isolation Pattern (Confirmed Safe)

Every plugin in this codebase follows strict SDK-only referencing:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\DataWarehouse.SDK\DataWarehouse.SDK.csproj" />
</ItemGroup>
```

**Verified:** Zero deprecated plugins reference other deprecated plugins. Zero non-deprecated code references deprecated plugins. This makes removal mechanically safe.

### Test Project Pattern

The test project (DataWarehouse.Tests.csproj) uses `<Compile Remove>` entries to exclude test files that reference deprecated/removed functionality:

```xml
<ItemGroup>
  <Compile Remove="PluginTests.cs" />
  <Compile Remove="Replication/GeoReplicationPluginTests.cs" />
  <Compile Remove="Database/RelationalDatabasePluginTests.cs" />
  <!-- etc. -->
</ItemGroup>
```

These excluded .cs files still exist on disk (17 files) and should be deleted as part of cleanup.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| slnx editing | Custom XML parser | Direct text manipulation (remove `<Project>` lines) | slnx is simple XML; each plugin is a single line |
| Dependency verification | Manual code review | `grep -r` / `rg` for namespace references | Automated scanning is exhaustive; manual review misses things |
| Build verification | Partial build | `dotnet clean && dotnet build` full solution | Must verify the entire solution compiles, not just parts |
| File cleanup | Manual per-directory deletion | Scripted batch deletion with a verified list | 88 directories is too many for manual operation |

## Common Pitfalls

### Pitfall 1: Overcounting from Stale Analysis
**What goes wrong:** The DEPRECATION-ANALYSIS.md claims 127+ plugins. Attempting to delete directories that don't exist causes script failures or confusion.
**Why it happens:** The analysis was written before prior phases completed some cleanup (Compression plugins, Encryption plugins already removed).
**How to avoid:** Use the verified 88-plugin list from this research, not the 127+ count from the analysis.
**Warning signs:** File-not-found errors during deletion, unexpected directory counts.

### Pitfall 2: Stale Build Artifacts
**What goes wrong:** After removing projects from slnx and deleting source directories, the solution still "sees" old assemblies in bin/obj folders.
**Why it happens:** Previous builds left compiled DLLs in bin/obj that dotnet restore may still reference.
**How to avoid:** Run `dotnet clean` before the final build verification. Consider deleting bin/obj directories from the solution root.
**Warning signs:** Build succeeds but with warnings about missing project references; runtime errors loading old assemblies.

### Pitfall 3: Missing Directories That Are Still in slnx
**What goes wrong:** The slnx references a project whose directory was already deleted, causing build errors.
**Why it happens:** Previous cleanup phases deleted directories but forgot to remove the slnx entry.
**How to avoid:** This is actually the opposite scenario here -- we verified all 66 slnx-referenced deprecated plugins DO exist on disk. But always verify both directions.
**Warning signs:** "Project file not found" build errors.

### Pitfall 4: Orphaned Test Files
**What goes wrong:** Test files that reference deprecated plugin namespaces remain on disk. While currently excluded via `<Compile Remove>`, they create confusion and clutter.
**Why it happens:** `<Compile Remove>` is a workaround, not a solution. The files should be deleted entirely.
**How to avoid:** Delete the 17 excluded test files and remove their `<Compile Remove>` entries from the test csproj.
**Warning signs:** grep hits on deprecated namespaces in test files.

### Pitfall 5: Documentation References
**What goes wrong:** TODO.md, IMPROVEMENT_ROADMAP.md, and CODE_REVIEW_REPORT.md still reference deprecated plugin names.
**Why it happens:** Documentation updates are often forgotten during code cleanup.
**How to avoid:** After code cleanup, scan documentation for references to removed plugins and update as needed.
**Warning signs:** grep hits for deprecated plugin names in Metadata/ files.

### Pitfall 6: AirGapBridge and DataMarketplace Confusion
**What goes wrong:** These two KEEP plugins exist on disk but are NOT in the slnx. They could be mistakenly deleted or mistakenly added to slnx.
**Why it happens:** They appear in the same directory listing as deprecated plugins but have unique functionality.
**How to avoid:** Maintain a clear KEEP list that includes these two. Do NOT delete them. Whether to add them to slnx is a separate decision (out of scope for this phase).
**Warning signs:** Deletion scripts that use wildcard patterns instead of explicit lists.

## Verified Inventory

### Deprecated Plugins on Disk AND in slnx (66 plugins -- need slnx removal + directory deletion)

| # | Plugin Directory | Category |
|---|-----------------|----------|
| 1 | AccessLog | Operations |
| 2 | AccessPrediction | Misc Infrastructure |
| 3 | AdoNetProvider | Database Protocols |
| 4 | AIInterface | Interface Protocols |
| 5 | Alerting | Operations |
| 6 | AlertingOps | Operations |
| 7 | ApacheSuperset | Dashboards |
| 8 | AuditLogging | Operations |
| 9 | BatteryAware | Sustainability |
| 10 | BlueGreenDeployment | Deployment |
| 11 | CanaryDeployment | Deployment |
| 12 | CarbonAwareness | Sustainability |
| 13 | Chronograf | Dashboards |
| 14 | ContentProcessing | Misc Infrastructure |
| 15 | Datadog | Observability |
| 16 | DistributedTracing | Observability |
| 17 | DistributedTransactions | Misc Infrastructure |
| 18 | Docker | Deployment |
| 19 | Dynatrace | Observability |
| 20 | FilesystemCore | SDK Migrations |
| 21 | Geckoboard | Dashboards |
| 22 | GeoDistributedConsensus | Resilience |
| 23 | GrafanaLoki | Observability |
| 24 | GraphQlApi | Interface Protocols |
| 25 | GrpcInterface | Interface Protocols |
| 26 | HardwareAcceleration | SDK Migrations |
| 27 | HierarchicalQuorum | Resilience |
| 28 | HotReload | Resilience |
| 29 | Hypervisor | Deployment |
| 30 | Jaeger | Observability |
| 31 | JdbcBridge | Database Protocols |
| 32 | K8sOperator | Deployment |
| 33 | Kibana | Dashboards |
| 34 | LoadBalancer | Resilience |
| 35 | LogicMonitor | Observability |
| 36 | Logzio | Observability |
| 37 | LowLatency | SDK Migrations |
| 38 | Metabase | Dashboards |
| 39 | Metadata | SDK Migrations |
| 40 | MySqlProtocol | Database Protocols |
| 41 | Netdata | Observability |
| 42 | NewRelic | Observability |
| 43 | NoSqlProtocol | Database Protocols |
| 44 | OdbcDriver | Database Protocols |
| 45 | OpenTelemetry | Observability |
| 46 | OracleTnsProtocol | Database Protocols |
| 47 | Perses | Observability |
| 48 | PostgresWireProtocol | Database Protocols |
| 49 | PowerBI | Dashboards |
| 50 | Prometheus | Observability |
| 51 | Redash | Dashboards |
| 52 | Resilience | Resilience |
| 53 | RestInterface | Interface Protocols |
| 54 | RetryPolicy | Resilience |
| 55 | SchemaRegistry | Deployment |
| 56 | Search | Misc Infrastructure |
| 57 | SigNoz | Observability |
| 58 | SmartScheduling | Deployment |
| 59 | Splunk | Observability |
| 60 | SqlInterface | Interface Protocols |
| 61 | Tableau | Dashboards |
| 62 | TdsProtocol | Database Protocols |
| 63 | VictoriaMetrics | Observability |
| 64 | Zabbix | Observability |
| 65 | ZeroConfig | SDK Migrations |
| 66 | ZeroDowntimeUpgrade | Resilience |

### Deprecated Plugins on Disk but NOT in slnx (22 plugins -- need directory deletion only)

| # | Plugin Directory | Category |
|---|-----------------|----------|
| 1 | AdaptiveEc | RAID/Erasure |
| 2 | AdvancedRaid | RAID |
| 3 | AutoRaid | RAID |
| 4 | CrdtReplication | Replication |
| 5 | CrossRegion | Replication |
| 6 | EnhancedRaid | RAID |
| 7 | ErasureCoding | RAID/Erasure |
| 8 | ExtendedRaid | RAID |
| 9 | FanOutOrchestration | Misc Infrastructure |
| 10 | FederatedQuery | Replication |
| 11 | Federation | Replication |
| 12 | GeoReplication | Replication |
| 13 | IsalEc | RAID/Erasure |
| 14 | MultiMaster | Replication |
| 15 | NestedRaid | RAID |
| 16 | Raid | RAID |
| 17 | RealTimeSync | Replication |
| 18 | SelfHealingRaid | RAID |
| 19 | SharedRaidUtilities | RAID |
| 20 | StandardRaid | RAID |
| 21 | VendorSpecificRaid | RAID |
| 22 | ZfsRaid | RAID |

### KEEP Standalone Plugins (13 plugins -- DO NOT DELETE)

| Plugin | In slnx | Keep Reason |
|--------|---------|-------------|
| AdaptiveTransport | YES | T78 - Unique transport morphing |
| AirGapBridge | NO | T79 - Hardware USB/external |
| DataMarketplace | NO | T83 - Commerce/billing |
| SelfEmulatingObjects | YES | T86 - WASM format preservation |
| Compute.Wasm | YES | T111 - WASM compute |
| Transcoding.Media | YES | Media transcoding |
| Virtualization.SqlOverObject | YES | SQL virtualization |
| FuseDriver | YES | Linux FUSE driver |
| WinFspDriver | YES | Windows driver |
| KubernetesCsi | YES | Kubernetes CSI |
| Raft | YES | Raft consensus (used by T95) |
| TamperProof | YES | T5/T6 - Tamper-proof storage |
| AedsCore | YES | AEDS infrastructure |

### Ultimate/Universal Plugins (44 plugins -- DO NOT DELETE)

44 Ultimate/Universal plugin directories on disk, all in slnx. These are the modern strategy-based replacements.

## Orphaned Test Files (17 files)

These files exist in DataWarehouse.Tests/ but are excluded from compilation via `<Compile Remove>`:

| File | References Deprecated Plugin |
|------|------------------------------|
| PluginTests.cs | YES (AIAgents, Compression, Encryption, LocalStorage) |
| Replication/GeoReplicationPluginTests.cs | YES (GeoReplication) |
| Database/RelationalDatabasePluginTests.cs | YES (RelationalDatabaseStorage) |
| Database/EmbeddedDatabasePluginTests.cs | YES (EmbeddedDatabaseStorage) |
| Infrastructure/CircuitBreakerPolicyTests.cs | References removed SDK functionality |
| Infrastructure/MetricsCollectorTests.cs | References removed SDK functionality |
| Infrastructure/TokenBucketRateLimiterTests.cs | References removed SDK functionality |
| Infrastructure/SdkInfrastructureTests.cs | References removed SDK functionality |
| Infrastructure/CodeCleanupVerificationTests.cs | References removed SDK functionality |
| Telemetry/DistributedTracingTests.cs | References removed functionality |
| Hyperscale/ErasureCodingTests.cs | References removed functionality |
| Storage/DurableStateTests.cs | References removed functionality |
| Kernel/DataWarehouseKernelTests.cs | References removed functionality |
| Integration/KernelIntegrationTests.cs | References removed functionality |
| Messaging/AdvancedMessageBusTests.cs | References removed functionality |
| Pipeline/PipelineOrchestratorTests.cs | References removed functionality |
| Security/MpcStrategyTests.cs | References removed functionality |

**Recommendation:** Delete all 17 files and remove the 17 `<Compile Remove>` entries from DataWarehouse.Tests.csproj.

## Documentation References

Files in Metadata/ that reference deprecated plugin names:

| File | Impact |
|------|--------|
| TODO.md | Contains task entries referencing deprecated plugins; these are historical records -- update status to reflect removal |
| IMPROVEMENT_ROADMAP.md | References deprecated plugin names |
| CODE_REVIEW_REPORT.md | References deprecated plugin names |
| Incomplete Tasks.txt | References deprecated plugin names |

**Recommendation:** Mark T108 as complete in TODO.md. Documentation references are historical and can be left as-is (they document what was deprecated, which is useful context).

## Risk Assessment

**Risk Level:** LOW

**Rationale:**
1. All 88 deprecated plugins have zero external code references (verified via grep)
2. All plugins follow SDK-only isolation (verified via csproj inspection)
3. Test project already excludes deprecated-plugin test files
4. Kernel and SDK have zero references to deprecated plugins
5. No cross-dependencies between deprecated plugins
6. slnx is simple XML -- line removal is straightforward

**Rollback:** Git revert to restore all files if any issues discovered.

## Execution Approach

### Plan 18-01: Identify and Verify All Migrated Plugins
- Use the verified inventory from this research (88 plugins, not 127+)
- Cross-reference with DEPRECATION-ANALYSIS.md to confirm categorizations
- Produce the definitive deletion list

### Plan 18-02: Remove from slnx and Delete Files
- Remove 66 `<Project>` lines from DataWarehouse.slnx
- Delete all 88 deprecated plugin directories (3,673 files)
- Delete 17 orphaned test files from DataWarehouse.Tests/
- Remove 17 `<Compile Remove>` entries from DataWarehouse.Tests.csproj
- Run `dotnet clean` then `dotnet build` to verify

### Plan 18-03: Clean Up References and Verify Build
- Update T108 status in TODO.md
- Final grep scan for any remaining references to deleted plugins
- Verify clean build with zero warnings/errors related to removed projects
- Update `Plugin Readme.txt` or remove if obsolete

## Post-Cleanup Metrics

| Metric | Before | After | Change |
|--------|--------|-------|--------|
| Plugin directories on disk | 145 | 57 | -88 (60.7% reduction) |
| Plugin entries in slnx | 121 | 55 | -66 (54.5% reduction) |
| Files in deprecated dirs | ~3,673 | 0 | -3,673 files |
| Orphaned test files | 17 | 0 | -17 files |
| Compile Remove entries | 17 | 0 | -17 entries |

## Open Questions

1. **AirGapBridge and DataMarketplace**
   - What we know: These are KEEP plugins on disk but not in slnx
   - What's unclear: Should they be added to slnx as part of this cleanup?
   - Recommendation: Out of scope for Phase 18. Document but do not change.

2. **Plugin Readme.txt**
   - What we know: A "Plugin Readme.txt" is referenced in slnx under the Plugins folder
   - What's unclear: Whether its content is still accurate post-cleanup
   - Recommendation: Review and update as part of Plan 18-03.

## Sources

### Primary (HIGH confidence)
- DataWarehouse.slnx -- direct inspection of all 121 plugin entries
- Plugins/ directory listing -- direct inspection of all 145 directories
- All deprecated plugin .csproj files -- verified SDK-only references
- DataWarehouse.Tests.csproj -- verified Compile Remove entries
- DataWarehouse.Kernel.csproj -- verified no deprecated plugin references
- .planning/phases/18-plugin-cleanup/DEPRECATION-ANALYSIS.md -- Phase 14-05 analysis

### Secondary (MEDIUM confidence)
- Metadata/TODO.md -- task status and deprecation documentation (large file, spot-checked)

## Metadata

**Confidence breakdown:**
- Plugin inventory (88 count): HIGH - verified by direct file system + slnx inspection
- Safety of removal: HIGH - verified zero external references via automated grep
- Test file cleanup: HIGH - verified file existence and Compile Remove entries
- Documentation impact: MEDIUM - spot-checked TODO.md; full scan not performed on all metadata files

**Research date:** 2026-02-11
**Valid until:** No expiration -- this is a point-in-time inventory of the current codebase state
