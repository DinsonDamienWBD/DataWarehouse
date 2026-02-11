# Phase 13: Data Governance Intelligence - Research

**Researched:** 2026-02-11
**Domain:** Data governance intelligence -- lineage, catalog, quality, semantic, and governance strategies (T146)
**Confidence:** HIGH

## Summary

Phase 13 implements AI-native data governance intelligence across five strategy domains (lineage, catalog, quality, semantic, governance) as defined in TODO.md task T146 (UltimateDataSemantic). The codebase already contains significant infrastructure: four existing Ultimate plugins (UltimateDataLineage, UltimateDataCatalog, UltimateDataQuality, UltimateDataGovernance) provide base classes, registries, and basic strategies. Additionally, three AI-native strategies (ActiveLineage, SemanticUnderstanding, LivingCatalog) already exist inside UltimateIntelligence's DataSemantic folder.

The T146 task defines 23 "WIN" strategies across 5 blocks (B1-B5) that represent AI-enhanced, industry-first capabilities. These strategies MUST be added to the existing plugins -- NOT as a new "DataGovernanceIntelligence" standalone plugin. The task spec in TODO.md references the strategies by name and maps them to blocks B1-B5. The existing 3 strategies in UltimateIntelligence/DataSemantic overlap with T146.A1-A3 (which are Phase A SDK foundation items), but the 23 Phase B strategies (B1.1-B5.4) have NOT been implemented yet.

**Primary recommendation:** Implement the 23 T146.B strategies as new strategy classes across the existing Ultimate plugins (UltimateDataLineage for B1, UltimateDataCatalog for B2, UltimateDataQuality for B3, UltimateIntelligence for B4, UltimateDataGovernance for B5), following the established base class and registry patterns. Each strategy extends the respective plugin's strategy base class and uses message bus for cross-plugin communication with graceful degradation.

## Standard Stack

### Core -- Existing Plugins (NO new plugins needed)

| Plugin | Task | Strategies Exist | Purpose |
|--------|------|-----------------|---------|
| UltimateDataLineage | T131 | 13 strategies (8 categories) | Lineage tracking, provenance, impact |
| UltimateDataCatalog | T128 | 80 strategies (8 categories) | Asset discovery, schema, search |
| UltimateDataQuality | T134 | 19 strategies (8 categories) | Validation, profiling, monitoring |
| UltimateDataGovernance | T123 | 73 strategies (8 categories) | Policy, compliance, classification |
| UltimateIntelligence | T90 | 137+ strategies (7 categories) | AI providers, vectors, knowledge graphs |

### Strategy Base Classes Per Plugin

| Plugin | Interface | Base Class | Registry |
|--------|-----------|------------|----------|
| UltimateDataLineage | `ILineageStrategy` | `LineageStrategyBase` | `LineageStrategyRegistry` |
| UltimateDataCatalog | `IDataCatalogStrategy` | `DataCatalogStrategyBase` | `DataCatalogStrategyRegistry` |
| UltimateDataQuality | `IDataQualityStrategy` | `DataQualityStrategyBase` | `DataQualityStrategyRegistry` |
| UltimateDataGovernance | `IDataGovernanceStrategy` | `DataGovernanceStrategyBase` | `DataGovernanceStrategyRegistry` |
| UltimateIntelligence | `IIntelligenceStrategy` | `IntelligenceStrategyBase` | `IntelligenceStrategyRegistry` |

### SDK Dependencies

| Component | Location | Purpose |
|-----------|----------|---------|
| `FeaturePluginBase` | SDK/Contracts | Base for non-intelligence-aware plugins |
| `IntelligenceAwareDataManagementPluginBase` | SDK/Contracts/IntelligenceAware | Base for governance plugin (AI-aware) |
| `IntelligenceAwarePluginBase` | SDK/Contracts/IntelligenceAware | Base for intelligence-aware plugins |
| `IMessageBus` | SDK/Contracts | Inter-plugin communication |
| `PluginMessage` | SDK/Primitives | Message format |
| AI types | SDK/AI/ | `IAIProvider`, `VectorOperations`, `GraphStructures`, `MathUtilities` |

## Architecture Patterns

### T146.B Strategy Mapping to Plugins

```
T146.B1 (Active Lineage) --> UltimateDataLineage/Strategies/
  B1.1 SelfTrackingDataStrategy
  B1.2 RealTimeLineageCaptureStrategy
  B1.3 LineageInferenceStrategy
  B1.4 ImpactAnalysisEngineStrategy
  B1.5 LineageVisualizationStrategy

T146.B2 (Living Catalog) --> UltimateDataCatalog/Strategies/
  B2.1 SelfLearningCatalogStrategy
  B2.2 AutoTaggingStrategy
  B2.3 RelationshipDiscoveryStrategy
  B2.4 SchemaEvolutionTrackerStrategy
  B2.5 UsagePatternLearnerStrategy

T146.B3 (Predictive Quality) --> UltimateDataQuality/Strategies/
  B3.1 QualityAnticipatorStrategy
  B3.2 DataDriftDetectorStrategy
  B3.3 AnomalousDataFlagStrategy
  B3.4 QualityTrendAnalyzerStrategy
  B3.5 RootCauseAnalyzerStrategy

T146.B4 (Semantic Understanding) --> UltimateIntelligence/Strategies/DataSemantic/
  B4.1 SemanticMeaningExtractorStrategy
  B4.2 ContextualRelevanceStrategy
  B4.3 DomainKnowledgeIntegratorStrategy
  B4.4 CrossSystemSemanticMatchStrategy

T146.B5 (Intelligent Governance) --> UltimateDataGovernance/Strategies/
  B5.1 PolicyRecommendationStrategy
  B5.2 ComplianceGapDetectorStrategy
  B5.3 SensitivityClassifierStrategy
  B5.4 RetentionOptimizerStrategy
```

### File Structure Per Block

```
# B1 example - Lineage strategies
Plugins/DataWarehouse.Plugins.UltimateDataLineage/
  Strategies/
    LineageStrategies.cs                  # Existing (5 basic strategies)
    AdvancedLineageStrategies.cs          # Existing (8 advanced strategies)
    ActiveLineageStrategies.cs            # NEW - T146.B1.1-B1.5

# B2 example - Catalog strategies
Plugins/DataWarehouse.Plugins.UltimateDataCatalog/
  Strategies/
    AssetDiscovery/
      AssetDiscoveryStrategies.cs         # Existing (10 strategies)
    LivingCatalog/                        # NEW folder
      LivingCatalogStrategies.cs          # NEW - T146.B2.1-B2.5

# B3 example - Quality strategies
Plugins/DataWarehouse.Plugins.UltimateDataQuality/
  Strategies/
    Monitoring/
      MonitoringStrategies.cs             # Existing (2 strategies)
    PredictiveQuality/                    # NEW folder
      PredictiveQualityStrategies.cs      # NEW - T146.B3.1-B3.5

# B4 example - Semantic strategies
Plugins/DataWarehouse.Plugins.UltimateIntelligence/
  Strategies/
    DataSemantic/
      DataSemanticStrategies.cs           # Existing (3 strategies)
      SemanticIntelligenceStrategies.cs   # NEW - T146.B4.1-B4.4

# B5 example - Governance strategies
Plugins/DataWarehouse.Plugins.UltimateDataGovernance/
  Strategies/
    PolicyManagement/
      PolicyManagementStrategies.cs       # Existing (10 strategies)
    IntelligentGovernance/                # NEW folder
      IntelligentGovernanceStrategies.cs  # NEW - T146.B5.1-B5.4
```

### Pattern 1: Lineage Strategy (extends LineageStrategyBase)

**What:** New lineage strategies implement `ILineageStrategy` via `LineageStrategyBase`
**When to use:** B1.1-B1.5 strategies
**Example:**

```csharp
// Source: Plugins/DataWarehouse.Plugins.UltimateDataLineage/Strategies/LineageStrategies.cs
public sealed class SelfTrackingDataStrategy : LineageStrategyBase
{
    public override string StrategyId => "active-self-tracking";
    public override string DisplayName => "Self-Tracking Data";
    public override LineageCategory Category => LineageCategory.Origin;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true,
        SupportsDownstream = true,
        SupportsTransformations = true,
        SupportsSchemaEvolution = true,
        SupportsImpactAnalysis = true,
        SupportsVisualization = true,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "Self-tracking data strategy: data objects maintain their own lineage history, " +
        "recording every transformation, copy, and derivation automatically.";
    public override string[] Tags => ["self-tracking", "automatic", "history", "industry-first"];

    // Must implement: GetUpstreamAsync, GetDownstreamAsync, AnalyzeImpactAsync
    // Can override: TrackAsync, InitializeCoreAsync, DisposeCoreAsync
}
```

### Pattern 2: Catalog Strategy (extends DataCatalogStrategyBase)

**What:** Catalog strategies are simpler -- just metadata plus capabilities
**When to use:** B2.1-B2.5 strategies

```csharp
// Source: Plugins/DataWarehouse.Plugins.UltimateDataCatalog/Strategies/*/
public sealed class SelfLearningCatalogStrategy : DataCatalogStrategyBase
{
    public override string StrategyId => "living-self-learning";
    public override string DisplayName => "Self-Learning Catalog";
    public override DataCatalogCategory Category => DataCatalogCategory.AssetDiscovery;
    public override DataCatalogCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsFederation = true,
        SupportsVersioning = true,
        SupportsMultiTenancy = true
    };
    public override string SemanticDescription => "...";
    public override string[] Tags => [...];
}
```

### Pattern 3: Quality Strategy (extends DataQualityStrategyBase)

**What:** Quality strategies include stats tracking via base class helpers
**When to use:** B3.1-B3.5 strategies

```csharp
// Source: Plugins/DataWarehouse.Plugins.UltimateDataQuality/Strategies/*/
public sealed class QualityAnticipatorStrategy : DataQualityStrategyBase
{
    public override string StrategyId => "predictive-anticipator";
    public override string DisplayName => "Quality Anticipator";
    public override DataQualityCategory Category => DataQualityCategory.Monitoring;
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true
    };
    // Use base class helpers: RecordProcessed(), RecordFailure(), UpdateQualityScore()
}
```

### Pattern 4: Intelligence Strategy (extends IntelligenceStrategyBase)

**What:** Semantic strategies live in UltimateIntelligence and extend `IntelligenceStrategyBase`
**When to use:** B4.1-B4.4 strategies

```csharp
// Source: Plugins/DataWarehouse.Plugins.UltimateIntelligence/Strategies/DataSemantic/
public sealed class SemanticMeaningExtractorStrategy : IntelligenceStrategyBase
{
    public override string StrategyId => "data-semantic-meaning-extractor";
    public override string StrategyName => "Semantic Meaning Extractor";
    public override IntelligenceStrategyCategory Category => IntelligenceStrategyCategory.Feature;
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Semantic Meaning Extractor",
        Description = "Extracts semantic meaning from data, not just structure",
        Capabilities = IntelligenceCapabilities.Classification | IntelligenceCapabilities.SemanticSearch,
        CostTier = 3,
        LatencyTier = 3,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "semantic", "meaning", "extraction", "nlp" },
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>()
    };
}
```

### Pattern 5: Governance Strategy (extends DataGovernanceStrategyBase)

**What:** Governance strategies are metadata-only extending `DataGovernanceStrategyBase`
**When to use:** B5.1-B5.4 strategies

```csharp
// Source: Plugins/DataWarehouse.Plugins.UltimateDataGovernance/Strategies/*/
public sealed class PolicyRecommendationStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "intelligent-policy-recommendation";
    public override string DisplayName => "AI Policy Recommendation";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsRealTime = true,
        SupportsAudit = true,
        SupportsVersioning = true
    };
    public override string SemanticDescription =>
        "AI-powered policy recommendation engine that analyzes data patterns " +
        "and recommends governance policies automatically.";
    public override string[] Tags => ["ai", "recommendation", "policy", "intelligent"];
}
```

### AI Graceful Degradation Pattern

All T146 strategies are AI-enhanced but MUST degrade gracefully:

```csharp
// Message bus pattern for AI features
var response = await _messageBus.RequestAsync<IntelligenceResponse>(
    topic: "intelligence.embeddings.generate",
    request: new EmbeddingRequest { Content = data },
    timeout: TimeSpan.FromSeconds(30),
    ct: ct
);

if (!response.Success)
{
    // Fallback: use heuristic-based approach instead of AI
    _logger.LogWarning("Intelligence plugin unavailable, using heuristic fallback");
    return HeuristicFallback(data);
}
```

### Anti-Patterns to Avoid

- **Creating a new standalone DataGovernanceIntelligence plugin:** All strategies go into EXISTING plugins
- **Direct plugin-to-plugin references:** Use message bus only
- **Stub implementations:** Rule 13 mandates production-ready implementations
- **AI-only implementations without fallbacks:** Every AI feature needs a heuristic fallback
- **Duplicate type definitions:** The UltimateIntelligence DataSemantic file already defines types like `LineageNode`, `SemanticProfile`, etc. -- B4 strategies should reuse those types, not redeclare them

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Graph traversal | Custom graph library | Existing `LineageStrategyBase._nodes/_edges` + BFS | Base class provides thread-safe graph storage |
| Strategy discovery | Manual registration | `AutoDiscover()` via reflection | All registries support auto-discovery from assembly |
| Statistics tracking | Custom counters | `DataQualityStrategyBase.RecordProcessed()` etc. | Thread-safe stats in base class |
| Semantic similarity | Custom embeddings | `VectorOperations.CosineSimilarity()` from SDK/AI | SDK provides math utilities |
| Knowledge graphs | Custom graph DB | `IKnowledgeGraph` from SDK/AI/GraphStructures.cs | SDK has full knowledge graph support |

**Key insight:** Every plugin already has a robust base class, registry with auto-discovery, and established patterns. The T146 strategies slot into these existing frameworks. The UltimateIntelligence DataSemantic folder already has 3 working strategies with full type definitions that serve as a template.

## Common Pitfalls

### Pitfall 1: Strategy ID Naming Collisions
**What goes wrong:** New strategy IDs clash with existing ones in the same registry
**Why it happens:** Each registry uses StrategyId as the key in a ConcurrentDictionary
**How to avoid:** Use a naming convention: `{block}-{descriptive-name}` e.g., `active-self-tracking`, `living-self-learning`, `predictive-anticipator`
**Warning signs:** Registry.Count is lower than expected after auto-discovery

### Pitfall 2: Type Name Collisions Across Plugins
**What goes wrong:** Types like `LineageNode` exist in both UltimateDataLineage AND UltimateIntelligence.DataSemantic
**Why it happens:** The existing 3 strategies in UltimateIntelligence define their own type definitions
**How to avoid:** B1 strategies in UltimateDataLineage use its OWN `LineageNode`/`LineageEdge` types. B4 strategies in UltimateIntelligence use the DataSemantic types. Never cross-reference.
**Warning signs:** Ambiguous type reference compile errors

### Pitfall 3: Forgetting to Match Existing Category Enums
**What goes wrong:** New strategies don't map to existing categories, so they won't be discoverable
**Why it happens:** Each plugin has different category enums (LineageCategory, DataCatalogCategory, etc.)
**How to avoid:** Check the existing enum values and map new strategies to them. May need to add new enum values.
**Warning signs:** GetByCategory() returns 0 for new strategy types

### Pitfall 4: Missing Production Logic in Strategies
**What goes wrong:** Strategies look complete but contain placeholder logic (violates Rule 13)
**Why it happens:** Some existing strategies return empty arrays for GetUpstreamAsync/GetDownstreamAsync
**How to avoid:** Every method must have real algorithmic logic. Use heuristic approaches where AI is unavailable.
**Warning signs:** Methods return empty collections or hardcoded values

### Pitfall 5: Not Updating Plugin Orchestrator for New Categories
**What goes wrong:** New strategies are discovered but not accessible via message handlers
**Why it happens:** Plugin's OnMessageAsync switch only handles existing message types
**How to avoid:** For each block, verify the plugin orchestrator's message handler can route to new strategies. May need to add new message types.
**Warning signs:** Message bus requests return "not found" for new strategy capabilities

### Pitfall 6: Overlap with Existing DataSemantic Strategies
**What goes wrong:** T146.B1-B3 strategies duplicate functionality already in UltimateIntelligence/DataSemantic
**Why it happens:** ActiveLineageStrategy, SemanticUnderstandingStrategy, and LivingCatalogStrategy already exist as T146.A strategies
**How to avoid:** The A-phase strategies in UltimateIntelligence are INTELLIGENCE strategies (AI-first). The B-phase strategies in domain plugins are DOMAIN strategies (governance-first, AI-enhanced). They complement, not duplicate.
**Warning signs:** Functionally identical strategy pairs across plugins

## Code Examples

### Verified: Lineage Strategy with Full Graph Traversal
```csharp
// Source: UltimateDataLineage/Strategies/LineageStrategies.cs - InMemoryGraphStrategy
// This shows the CORRECT pattern for GetUpstreamAsync with real BFS
public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
{
    var visited = new HashSet<string>();
    var nodes = new List<LineageNode>();
    var edges = new List<LineageEdge>();
    var queue = new Queue<(string id, int depth)>();
    queue.Enqueue((nodeId, 0));

    while (queue.Count > 0)
    {
        var (currentId, depth) = queue.Dequeue();
        if (depth > maxDepth || visited.Contains(currentId)) continue;
        visited.Add(currentId);

        if (_nodes.TryGetValue(currentId, out var node))
            nodes.Add(node);

        if (_upstreamLinks.TryGetValue(currentId, out var uplinks))
        {
            foreach (var upId in uplinks)
            {
                queue.Enqueue((upId, depth + 1));
                edges.Add(new LineageEdge { ... });
            }
        }
    }

    return Task.FromResult(new LineageGraph { ... });
}
```

### Verified: Intelligence Strategy with Type Definitions
```csharp
// Source: UltimateIntelligence/Strategies/DataSemantic/DataSemanticStrategies.cs
// This shows the pattern for semantic strategies with full record types
public sealed class SemanticUnderstandingStrategy : IntelligenceStrategyBase
{
    // Full implementation with:
    // - DetectSemanticTypes() using regex patterns
    // - ExtractConcepts() using capitalized phrase extraction
    // - InferContext() using keyword matching
    // - ExtractEntities() using name pattern matching
    // - GenerateSemanticFingerprint() using SHA256
    // - CalculateSimilarity() using concept + type overlap
}
```

### Verified: Governance Strategy (Minimal Pattern)
```csharp
// Source: UltimateDataGovernance/Strategies/PolicyManagement/PolicyManagementStrategies.cs
// This shows the minimal governance strategy pattern
public sealed class PolicyDefinitionStrategy : DataGovernanceStrategyBase
{
    public override string StrategyId => "policy-definition";
    public override string DisplayName => "Policy Definition";
    public override GovernanceCategory Category => GovernanceCategory.PolicyManagement;
    public override DataGovernanceCapabilities Capabilities => new()
    {
        SupportsAsync = true, SupportsBatch = true,
        SupportsRealTime = true, SupportsAudit = true, SupportsVersioning = true
    };
    public override string SemanticDescription => "Define and manage governance policies with templates, rules, and metadata";
    public override string[] Tags => ["policy", "definition", "templates", "rules"];
}
```

## Existing Strategy Count Summary

| Plugin | Existing Strategies | T146.B Strategies to Add |
|--------|--------------------:|-------------------------:|
| UltimateDataLineage | 13 | 5 (B1.1-B1.5) |
| UltimateDataCatalog | 80 | 5 (B2.1-B2.5) |
| UltimateDataQuality | 19 | 5 (B3.1-B3.5) |
| UltimateIntelligence/DataSemantic | 3 | 4 (B4.1-B4.4) |
| UltimateDataGovernance | 73 | 4 (B5.1-B5.4) |
| **Total** | **188** | **23** |

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Static lineage (scan-based) | Active lineage (event-driven) | T146 design | Data knows its own history |
| Manual data cataloging | Self-learning catalog | T146 design | Catalog improves automatically |
| Reactive quality checks | Predictive quality | T146 design | Catch issues before they happen |
| Keyword-based search | Semantic understanding | T146 design | Understand MEANING not just structure |
| Manual policy creation | AI policy recommendations | T146 design | Governance policies auto-generated |

## Key Implementation Decisions

### Where Do T146 Strategies Live?

The TODO.md says T146 is "UltimateDataSemantic" and the top-level says "Complete - 3 strategies (ActiveLineage, SemanticUnderstanding, LivingCatalog)". These 3 exist in `UltimateIntelligence/Strategies/DataSemantic/`. But T146's Phase B defines 23 strategies that span 5 domains (lineage, catalog, quality, semantic, governance).

**Decision:** The 23 Phase B strategies should be distributed to their DOMAIN plugins:
- B1 (lineage strategies) -> UltimateDataLineage (has `LineageStrategyBase`)
- B2 (catalog strategies) -> UltimateDataCatalog (has `DataCatalogStrategyBase`)
- B3 (quality strategies) -> UltimateDataQuality (has `DataQualityStrategyBase`)
- B4 (semantic strategies) -> UltimateIntelligence/DataSemantic (has `IntelligenceStrategyBase`)
- B5 (governance strategies) -> UltimateDataGovernance (has `DataGovernanceStrategyBase`)

**Rationale:** Each plugin already has the correct base class, registry, and auto-discovery. Putting lineage strategies in UltimateDataLineage is consistent with the plugin consolidation rule.

### What About T146.A (SDK Foundation)?

T146.A1-A4 define SDK interfaces: `IDataSemantic`, `SemanticUnderstanding` types, `ActiveLineage` abstractions, `LivingCatalog` types. A search of the SDK found NO matches for these interfaces. However, the existing implementations in UltimateIntelligence/DataSemantic define all needed types locally (LineageNode, SemanticProfile, CatalogEntry, etc.).

**Decision:** The planner should verify whether SDK-level interfaces are needed. If the domain plugins need to share types, those types should go in the SDK. If not, each plugin keeps its own types (current pattern). This is an open question.

## Open Questions

1. **SDK Interface Gap (T146.A)**
   - What we know: TODO.md says T146.A1-A4 add SDK interfaces, but none exist. Types are defined locally in UltimateIntelligence.
   - What's unclear: Whether Phase A SDK interfaces need to be created first, or if local types suffice.
   - Recommendation: If strategies only need types within their own plugin, local types are fine. If cross-plugin message payloads reference these types, they should be in the SDK. The planner should check whether message bus communication between these 5 plugins requires shared types.

2. **Category Enum Extensions**
   - What we know: UltimateDataLineage has `LineageCategory` with 8 values. UltimateDataCatalog has `DataCatalogCategory` with 8 values. UltimateDataQuality has `DataQualityCategory` with 8 values.
   - What's unclear: Whether B1-B5 strategies fit into existing categories or need new enum values.
   - Recommendation: B1 lineage strategies can use existing categories (Origin, Transformation, Impact, Visualization). B2 catalog strategies can use AssetDiscovery, SearchDiscovery. B3 quality strategies can use Monitoring, Scoring. B5 governance strategies can use PolicyManagement, RegulatoryCompliance, DataClassification, RetentionManagement. Evaluate on a per-strategy basis.

3. **TODO.md Status Inconsistency**
   - What we know: The summary table says T146 is "[x] Complete - 3 strategies" but the detail section says "[ ] Not Started" for ALL B-phase sub-tasks.
   - What's unclear: Whether the 3 existing strategies were meant to satisfy all of T146 or just Phase A.
   - Recommendation: Treat the 3 existing strategies as Phase A (complete). All 23 Phase B strategies are NOT started and are the scope of Phase 13.

## Sources

### Primary (HIGH confidence)
- `Plugins/DataWarehouse.Plugins.UltimateDataLineage/` - Full source code review of plugin, base class, all 13 strategies
- `Plugins/DataWarehouse.Plugins.UltimateDataCatalog/` - Full source code review of plugin, base class, all 80 strategies
- `Plugins/DataWarehouse.Plugins.UltimateDataQuality/` - Full source code review of plugin, base class, all 19 strategies
- `Plugins/DataWarehouse.Plugins.UltimateDataGovernance/` - Full source code review of plugin, base class, all 73 strategies
- `Plugins/DataWarehouse.Plugins.UltimateIntelligence/Strategies/DataSemantic/` - Full source code review of 3 existing T146 strategies
- `DataWarehouse.SDK/Contracts/IntelligenceAware/SpecializedIntelligenceAwareBases.cs` - IntelligenceAwareDataManagementPluginBase
- `Metadata/TODO.md` (lines 15912-15977) - T146 task definition with all 27 sub-tasks
- `Metadata/CLAUDE.md` - Architecture rules, plugin patterns, Rule 13, Rule 14

### Secondary (MEDIUM confidence)
- Strategy counts from `grep -rc` across all strategy directories
- Category enum values from base class source files

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH - All 5 plugins examined in full source code, base classes verified
- Architecture: HIGH - Strategy patterns are consistent across all plugins, base classes well-documented
- Pitfalls: HIGH - Identified from actual code analysis (type collisions, category mapping, empty collections)
- Strategy mapping: HIGH - T146 sub-task descriptions match cleanly to plugin domains

**Research date:** 2026-02-11
**Valid until:** 2026-03-11 (stable codebase, no external dependencies)
