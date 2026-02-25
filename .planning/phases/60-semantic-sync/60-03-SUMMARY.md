---
phase: 60-semantic-sync
plan: 03
subsystem: SemanticSync Classification
tags: [semantic-sync, classification, ai-embedding, heuristics, edge, hybrid]
dependency_graph:
  requires: ["60-01 (SDK contracts)", "60-02 (plugin shell)"]
  provides: ["ISemanticClassifier implementations", "EmbeddingClassifier", "RuleBasedClassifier", "HybridClassifier"]
  affects: ["60-04 (routing)", "60-05 (conflict resolution)", "60-07 (edge inference)"]
tech_stack:
  added: []
  patterns: ["weighted fusion", "graceful degradation", "cosine similarity centroids", "Jaccard similarity", "multi-signal heuristic scoring"]
key_files:
  created:
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Classification/EmbeddingClassifier.cs
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Classification/RuleBasedClassifier.cs
    - Plugins/DataWarehouse.Plugins.SemanticSync/Strategies/Classification/HybridClassifier.cs
  modified: []
decisions:
  - "8-dimensional reference centroids as production baseline (refined by federated learning)"
  - "HybridClassifier as default classifier with 0.7/0.3 embedding/rule weighting"
  - "Rule-based confidence capped at 0.6-0.8 to honestly reflect heuristic limits"
metrics:
  duration: ~5min
  completed: 2026-02-19T19:51:00Z
  tasks_completed: 2
  tasks_total: 2
  files_created: 3
  files_modified: 0
---

# Phase 60 Plan 03: Semantic Classification Strategies Summary

Three production-ready semantic classifiers covering cloud, edge, and air-gapped deployments with AI-to-heuristic graceful degradation via weighted fusion.

## What Was Built

### EmbeddingClassifier
- Uses `IAIProvider.GetEmbeddingsAsync` to generate vector embeddings from data
- Compares embeddings against 8-dimensional reference centroids for each `SemanticImportance` level using cosine similarity
- Projects arbitrary-dimension embeddings to reference dimensionality via averaged pooling
- Batch classification via `GetEmbeddingsBatchAsync` with parallel centroid matching
- Semantic tag extraction from metadata keys (content-type, domain, source, category)
- Capabilities: SupportsEmbeddings=true, SupportsLocalInference=true, SupportsDomainHints=true, MaxBatchSize=100

### RuleBasedClassifier
- Zero AI dependency -- operates entirely on metadata heuristics
- Five-signal scoring system:
  1. **File size**: >100MB=High, <1KB=Low
  2. **Content-type**: JSON/XML with schema/config keywords=Critical; media=Normal
  3. **Metadata tags**: compliance/audit/security=Critical; log/temp/cache=Negligible
  4. **Recency**: modified <1h=bump, >30d=drop
  5. **Access frequency**: >100 accesses=High, <5=Low
- Confidence always 0.6-0.8 (honest heuristic bounds, more signals = higher within range)
- Jaccard similarity on tokenized data for `ComputeSemanticSimilarityAsync`
- Capabilities: SupportsEmbeddings=false, SupportsLocalInference=true, SupportsDomainHints=false, MaxBatchSize=1000

### HybridClassifier (DEFAULT)
- Composes both classifiers via constructor injection
- Parallel execution with `Task.WhenAll`
- Weighted fusion: ordinal importance averaging (Critical=4..Negligible=0) with configurable embedding weight (default 0.7)
- Combined confidence = embeddingWeight * embeddingConfidence + ruleWeight * ruleConfidence
- Semantic tag merging with deduplication
- Domain hint: embedding classifier preferred, falls back to rule-based
- Graceful degradation: if embedding classifier throws, seamlessly uses rule-based only
- Capabilities: union of both classifiers' capabilities

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| 1 | 7e5bbac0 | Embedding and rule-based classifiers |
| 2 | 7896a0a0 | Hybrid classifier with weighted fusion |

## Deviations from Plan

None -- plan executed exactly as written.

## Verification

- All 3 classifiers compile as part of the SemanticSync plugin project (0 errors, 0 warnings)
- Kernel build passes (`dotnet build DataWarehouse.Kernel/DataWarehouse.Kernel.csproj`)
- Each extends `SemanticSyncStrategyBase` and implements `ISemanticClassifier`
- EmbeddingClassifier uses `IAIProvider.GetEmbeddingsAsync` (confirmed via grep)
- RuleBasedClassifier has zero `IAIProvider` references (confirmed via grep)
- HybridClassifier delegates to both with weighted fusion and graceful fallback
- No stubs, mocks, placeholders, `Task.Delay`, or TODO markers
