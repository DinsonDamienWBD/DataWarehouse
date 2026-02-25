# 49-02: P0 Functionality Findings Inventory

**Status:** Inventory of critical functionality issues (data loss, corruption, blocking bugs)

## P0 Quality/Functionality Findings

### FIXED in Phase 43-04 (13 of 28 dispose-related)

| ID | Finding | Location | Fix | Commit |
|----|---------|----------|-----|--------|
| Q-P0-001 | Sync-over-async in AirGapBridge Dispose | StorageExtensionProvider.cs:488 | IAsyncDisposable | 6120fcf |
| Q-P0-002 | Sync-over-async in FuseDriver Dispose | FuseDriverPlugin.cs:805 | DisposeAsyncCore | 6120fcf |
| Q-P0-003 | Sync-over-async in BackgroundIntegrityScanner Dispose | BackgroundIntegrityScanner.cs:623 | IAsyncDisposable | 6120fcf |
| Q-P0-008 | Sync-over-async in TamperProofPlugin Dispose | TamperProofPlugin.cs:1014 | DisposeAsyncCore | 2b739d3 |
| Q-P0-020-024 | Timer callbacks blocking threadpool (4 timers + init) | RamDiskStrategy.cs:95,106,115,122 | Task.Run wrappers + async init | 5c973b7 |
| Q-P0-025-028 | Property getter lazy init blocking (5 properties) | PlatformCapabilityRegistry.cs:128-240 | InitializeAsync + throw | f1c8c41 |

### UNFIXED -- Dispose Method Blocking (15 remaining)

| ID | Finding | Location | Effort |
|----|---------|----------|--------|
| Q-P0-004-007 | FuseFileSystem SaveToStorageAsync in Dispose (3 sites) | FuseFileSystem.cs:368,910,1684 | 1h |
| Q-P0-009 | OrphanCleanupService StopAsync in Dispose | OrphanCleanupService.cs:497 | 20min |
| Q-P0-010 | UltimateKeyManagement StopAsync in Dispose | UltimateKeyManagementPlugin.cs:647 | 20min |
| Q-P0-011 | KeyRotationScheduler StopAsync in Dispose | KeyRotationScheduler.cs:323 | 20min |
| Q-P0-012 | ZeroConfigClusterBootstrap StopAsync in Dispose | ZeroConfigClusterBootstrap.cs:220 | 20min |
| Q-P0-013 | DatabaseStorageStrategyBase DisposeAsyncCore blocking | DatabaseStorageStrategyBase.cs:785 | 20min |
| Q-P0-014 | StorageStrategyBase DisposeCoreAsync blocking | StorageStrategyBase.cs:151 | 20min |
| Q-P0-015 | DatabaseProtocolStrategyBase DisconnectAsync in Dispose | DatabaseProtocolStrategyBase.cs:1132 | 20min |
| Q-P0-016 | AccessAuditLoggingStrategy ForceFlushAsync in Dispose | AccessAuditLoggingStrategy.cs:738 | 20min |
| Q-P0-017 | CLILearningStore SaveAsync in Dispose | CLILearningStore.cs:582 | 20min |
| Q-P0-018 | MdnsServiceDiscovery StopAsync in Dispose | MdnsServiceDiscovery.cs:460 | 20min |
| Q-P0-019 | ConnectionPoolManager ClearAsync in Dispose | ConnectionPoolManager.cs:630 | 20min |

### UNFIXED -- Domain Audit Functionality Bugs

| ID | Finding | Source Phase | Location | Severity | Effort |
|----|---------|-------------|----------|----------|--------|
| D-M1 | Decompression strategy uses entropy on compressed data (selects wrong algorithm) | 44-01 | UltimateCompressionPlugin.cs:184 | MEDIUM (data integrity) | 4-6h |
| D-M3 | PNG compression uses HMAC-SHA256 instead of DEFLATE | 44-03 | PngImageStrategy.cs | MEDIUM (produces corrupt files) | 4-6h |

## Summary

- **Total P0 Functionality Findings:** 30 (dispose) + 2 (domain bugs) = 32
- **Fixed:** 15 (Phase 43-04 dispose fixes)
- **Remaining Dispose:** 15 (well-defined pattern, 4-6h total)
- **Remaining Domain Bugs:** 2 (8-12h total)
- **Total Remaining Effort:** 12-18 hours

## Notes

- All 15 remaining dispose fixes follow the identical pattern demonstrated by the 13 already fixed
- The PlatformCapabilityRegistry fix introduced a breaking change (requires InitializeAsync before use)
- Domain bug D-M1 affects correctness of decompression path (stores compression algo ID needed)
- Domain bug D-M3 makes PNG transcoding produce unreadable files
