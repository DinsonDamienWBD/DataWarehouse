# Code Quality Fixes Plan

## Task 1: GetAwaiter().GetResult() - 48 instances

### High Priority (Dispose Methods - Deadlock Risk)
1. PluginRegistry.cs - Dispose method
2. ContainerManager.cs - Dispose method
3. CLILearningStore.cs - Dispose method
4. PortableMediaDetector.cs - Dispose method
5. ZeroConfigClusterBootstrap.cs - Dispose method
6. MdnsServiceDiscovery.cs - Dispose method

### Medium Priority (FUSE - Document as unavoidable)
7-11. FuseFileSystem.cs - 5 instances (FUSE API is synchronous)

### Low Priority (Convert to async or use Task.Run wrapper)
12-48. Remaining 37 instances in various plugins

## Task 2: new HttpClient() - 33 instances

### Critical (Per-Request Creation)
1. SseConnectionStrategy.cs:76 - using var testClient = new HttpClient() - should reuse client from handle

### Acceptable (Static readonly or parameterless constructor)
2-33. Most are already static readonly or constructor delegation patterns

## Task 3: null! Suppressions - 47 instances

### Review and Fix
1-47. Need to check each for required properties, nullable fields, or proper initialization

## Task 4: CLI Backup Simulation
1. DataWarehouse.CLI/Commands/BackupCommands.cs - Remove fake hardcoded values (lines 76, 96-99, 173-175)
