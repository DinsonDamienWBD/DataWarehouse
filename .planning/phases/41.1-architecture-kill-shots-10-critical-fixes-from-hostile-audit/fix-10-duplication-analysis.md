# FIX-10 Duplication Analysis

## Duplicated Methods

### 1. SelectOptimalAlgorithmAsync

**Current Locations:**
- `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/CompressionPluginBase.cs` (line 23)
  - Signature: `protected virtual Task<string> SelectOptimalAlgorithmAsync(Dictionary<string, object> context, CancellationToken ct = default)`
  - Default: returns `CompressionAlgorithm`
- `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/EncryptionPluginBase.cs` (line 29)
  - Signature: `protected virtual Task<string> SelectOptimalAlgorithmAsync(Dictionary<string, object> context, CancellationToken ct = default)`
  - Default: returns `AlgorithmId`

**Proposed Target:** `DataTransformationPluginBase` (lowest common ancestor of both Compression and Encryption)
- File: `DataWarehouse.SDK/Contracts/Hierarchy/DataPipeline/DataTransformationPluginBase.cs`

**Migration Strategy:**
1. Add generic `SelectOptimalAlgorithmAsync` to `DataTransformationPluginBase` with default returning `SubCategory` (generic fallback)
2. Add abstract `DefaultAlgorithmId` property for concrete algorithm identification
3. Remove local implementations from `CompressionPluginBase` and `EncryptionPluginBase`
4. Both leaf classes will inherit and can still override if needed

### 2. No Other Duplications Found

After reviewing all hierarchy classes:
- `CompressionPluginBase` has `PredictCompressionRatioAsync` - compression-specific, not duplicated
- `EncryptionPluginBase` has `EvaluateKeyStrengthAsync` and `DetectEncryptionAnomalyAsync` - encryption-specific, not duplicated
- `StoragePluginBase` has `OptimizeStoragePlacementAsync` and `PredictAccessPatternAsync` - storage-specific, not duplicated
- `DataTransitPluginBase`, `IntegrityPluginBase`, `ReplicationPluginBase` - no overlapping methods with other leaves

**Conclusion:** `SelectOptimalAlgorithmAsync` is the only duplicated pattern. Migration to `DataTransformationPluginBase` eliminates duplication while preserving override capability.
