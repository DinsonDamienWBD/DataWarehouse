# FIX-11 Legacy Base Migration Checklist

## Legacy Classes in PluginBase.cs (lines 1162-3222)

### 1. DataTransformationPluginBase (line 1162)
**Internal users (within PluginBase.cs):**
- `PipelinePluginBase` inherits from it

**External users (SDK/plugins):**
- `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/EncryptionPluginBase.cs` - uses NEW hierarchy version
- `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/CompressionPluginBase.cs` - uses NEW hierarchy version

**Action:** DELETE. Hierarchy version in `DataPipeline/DataTransformationPluginBase.cs` replaces this.

### 2. StorageProviderPluginBase (line 1252)
**Internal users:**
- `ListableStoragePluginBase` inherits from it

**External users:**
- `DataWarehouse.Kernel/Plugins/InMemoryStoragePlugin.cs` - MUST MIGRATE
- `DataWarehouse.SDK/Contracts/LowLatencyPluginBases.cs` (LowLatencyStoragePluginBase) - MUST MIGRATE

**Action:** Migrate InMemoryStoragePlugin and LowLatencyStoragePluginBase, then DELETE.

### 3. SecurityProviderPluginBase (line 1347)
**Internal users:**
- `AccessControlPluginBase` inherits from it

**External users:**
- `DataWarehouse.SDK/Contracts/MilitarySecurityPluginBases.cs` (4 classes) - MUST MIGRATE

**Action:** Migrate MilitarySecurityPluginBases, then DELETE.

### 4. InterfacePluginBase (line 1373)
**External users:** None (already [Obsolete], redirects to hierarchy version)
**Action:** DELETE.

### 5. PipelinePluginBase (line 1392)
**Internal users:**
- Legacy `EncryptionPluginBase` (line 2112) inherits from it
- Legacy `CompressionPluginBase` (line 2948) inherits from it

**External users:**
- `DataWarehouse.SDK/Contracts/TransitEncryptionPluginBases.cs` (TransitEncryptionPluginBase) - MUST MIGRATE
- `Plugins/UltimateIntelligence/UltimateIntelligencePlugin.cs` - MUST MIGRATE

**Action:** Migrate TransitEncryptionPluginBase and UltimateIntelligencePlugin, then DELETE.

### 6. ListableStoragePluginBase (line 1433)
**Internal users:**
- `TieredStoragePluginBase` inherits from it
- `CacheableStoragePluginBase` inherits from it

**External users:** None outside PluginBase.cs
**Action:** DELETE (chain features ported to new StoragePluginBase).

### 7. TieredStoragePluginBase (line 1453)
**External users:** None
**Action:** DELETE.

### 8. CacheableStoragePluginBase (line 1485)
**Internal users:**
- `IndexableStoragePluginBase` inherits from it

**External users:** None outside PluginBase.cs
**Action:** DELETE (features ported to new StoragePluginBase).

### 9. IndexableStoragePluginBase (line 1720)
**External users:**
- `DataWarehouse.SDK/Database/HybridDatabasePluginBase.cs` - MUST MIGRATE
- `DataWarehouse.SDK/Storage/HybridStoragePluginBase.cs` - MUST MIGRATE

**Action:** Migrate HybridDatabasePluginBase and HybridStoragePluginBase, then DELETE.

### 10. ConsensusPluginBase (line 1984)
**External users:**
- `Plugins/DataWarehouse.Plugins.Raft/RaftConsensusPlugin.cs` - MUST MIGRATE

**Action:** Migrate RaftConsensusPlugin, then DELETE.

### 11. ReplicationPluginBase (line 2049)
**External users:** None (already [Obsolete], hierarchy version exists)
**Action:** DELETE.

### 12. AccessControlPluginBase (line 2091)
**External users:** None
**Action:** DELETE.

### 13. EncryptionPluginBase (line 2112)
**External users:** None (legacy features ported to hierarchy version)
**Action:** DELETE.

### 14. CompressionPluginBase (line 2948)
**External users:** None
**Action:** DELETE.

### 15. ContainerManagerPluginBase (line 3137)
**External users:**
- `DataWarehouse.Kernel/Storage/ContainerManager.cs` - MUST MIGRATE

**Action:** Migrate ContainerManager, then DELETE.

### Helper Types
- `EncryptionStatistics` record (line 2929) - KEEP (moved to hierarchy EncryptionPluginBase or standalone)
- `ClusterState` class (line 2032) - CHECK references before deletion

## Migration Summary

### Files Requiring Migration (10 files):

| File | Current Base | Target Base |
|------|-------------|-------------|
| `DataWarehouse.Kernel/Plugins/InMemoryStoragePlugin.cs` | `StorageProviderPluginBase` | `Hierarchy.StoragePluginBase` |
| `DataWarehouse.SDK/Contracts/LowLatencyPluginBases.cs` | `StorageProviderPluginBase` | `Hierarchy.StoragePluginBase` |
| `DataWarehouse.SDK/Contracts/MilitarySecurityPluginBases.cs` (4 classes) | `SecurityProviderPluginBase` | `Hierarchy.SecurityPluginBase` |
| `DataWarehouse.SDK/Contracts/TransitEncryptionPluginBases.cs` | `PipelinePluginBase` | `Hierarchy.DataTransformationPluginBase` |
| `Plugins/UltimateIntelligence/UltimateIntelligencePlugin.cs` | `PipelinePluginBase` | `Hierarchy.DataTransformationPluginBase` |
| `DataWarehouse.SDK/Database/HybridDatabasePluginBase.cs` | `IndexableStoragePluginBase` | `Hierarchy.StoragePluginBase` + EnableIndexing |
| `DataWarehouse.SDK/Storage/HybridStoragePluginBase.cs` | `IndexableStoragePluginBase` | `Hierarchy.StoragePluginBase` + EnableIndexing |
| `Plugins/Raft/RaftConsensusPlugin.cs` | `ConsensusPluginBase` | `Hierarchy.InfrastructurePluginBase` |
| `DataWarehouse.Kernel/Storage/ContainerManager.cs` | `ContainerManagerPluginBase` | `Hierarchy.ComputePluginBase` |

### Types to Preserve (move if needed):
- `EncryptionStatistics` record - already exists in SDK Security namespace
- `ClusterState` class - may need standalone file
