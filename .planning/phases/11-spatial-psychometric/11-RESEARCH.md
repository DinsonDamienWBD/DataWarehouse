# Phase 11: Spatial & Psychometric - Research

**Researched:** 2026-02-11
**Domain:** AR spatial anchoring and psychometric content analysis
**Confidence:** MEDIUM

## Summary

Phase 11 implements two advanced capabilities: AR spatial anchors (T87) for location-based data binding and psychometric indexing (T88) for sentiment/emotion analysis. Both features already have partial implementations in the DataWarehouse codebase.

**SpatialIndexStrategy** exists with comprehensive R-tree geospatial indexing (GPS coordinates, bounding boxes, k-NN search, GeoJSON support), but lacks AR-specific features (SLAM, proximity verification, visual anchoring). **PsychometricIndexingStrategy** exists with sentiment/emotion analysis powered by AI providers, but may need enhancement for deception detection signals.

**Primary recommendation:** Extend existing implementations rather than building from scratch. Add AR spatial anchor capabilities to SDK + UltimateDataManagement, enhance psychometric indexing with deception detection models.

## Standard Stack

### Core (Spatial Anchors - T87)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Azure Spatial Anchors SDK | 2.14+ | Cloud-based spatial anchor service | Microsoft's production AR platform with .NET Standard 2.0 support |
| System.Numerics.Vectors | Built-in | 3D coordinate math | Standard .NET library for spatial calculations |
| GeoJSON.Net | Latest | GeoJSON parsing/serialization | De-facto standard for geographic data interchange |

**Note:** Existing `SpatialIndexStrategy.cs` already implements R-tree geospatial indexing with GeoJSON support, GPS coordinates, and Haversine distance calculations. Missing: AR visual anchoring, SLAM integration, proximity verification.

### Core (Psychometric Indexing - T88)

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Azure.AI.TextAnalytics | 5.x+ | Sentiment/emotion analysis | Azure Cognitive Services - enterprise NLP with 94 language support |
| Existing AI Provider | Internal | Pluggable AI backend | Already integrated in PsychometricIndexingStrategy |

**Note:** Existing `PsychometricIndexingStrategy.cs` implements sentiment analysis, emotion detection (Plutchik/Ekman models), Big Five personality inference, vector storage for searchability. Missing: explicit deception detection signals.

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| ML.NET | 3.x+ | Local ML model hosting | Offline psychometric analysis, custom models |
| Accord.NET | 3.x+ | Statistical pattern recognition | Advanced emotion detection, biometric signals |
| OpenCV (EmguCV) | 4.x+ | Visual feature extraction for SLAM | If implementing custom SLAM vs Azure |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Azure Spatial Anchors | Unity ARFoundation/ARKit/ARCore | Lower cost but requires native mobile apps, no cloud persistence |
| Azure Text Analytics | OpenAI GPT-4 API | More flexible but higher cost, less structured output |
| Cloud services | Local ML.NET models | Offline capability but lower accuracy, no multi-language |

**Installation (NuGet):**
```bash
# Spatial (if using Azure Spatial Anchors)
dotnet add package Microsoft.Azure.SpatialAnchors
dotnet add package GeoJSON.Net

# Psychometric (if using Azure Cognitive Services)
dotnet add package Azure.AI.TextAnalytics

# Optional ML.NET for local models
dotnet add package Microsoft.ML
dotnet add package Microsoft.ML.TextAnalytics
```

## Architecture Patterns

### Recommended Project Structure

Already in place:
```
Plugins/
├── UltimateDataManagement/
│   └── Strategies/
│       └── Indexing/
│           ├── SpatialIndexStrategy.cs         # EXISTS - R-tree geospatial
│           └── (NEW) SpatialAnchorStrategy.cs  # ADD - AR anchors
├── UltimateIntelligence/
│   └── Strategies/
│       └── Features/
│           └── PsychometricIndexingStrategy.cs # EXISTS - sentiment/emotion
DataWarehouse.SDK/
├── Contracts/
│   ├── Spatial/                                # ADD - AR anchor interfaces
│   └── Intelligence/                           # EXISTS - AI contracts
```

### Pattern 1: Spatial Anchor Storage and Retrieval

**What:** Bind KnowledgeObjects to physical locations using GPS + visual features
**When to use:** AR applications, location-based data access, proximity triggers

**Conceptual flow:**
1. Create anchor: GPS coords + visual feature signature (SLAM)
2. Store anchor: Cloud service (Azure) or local database with visual hash
3. Retrieve: Query by GPS proximity (existing R-tree), verify by visual match
4. Validate: Proximity verification (distance threshold + confidence score)

**Code pattern (extending existing SpatialIndexStrategy):**
```csharp
// Existing spatial index for GPS-based queries
var nearbyObjects = await _spatialIndex.SearchNearestNeighbors(
    new { lat = 37.7749, lon = -122.4194, k = 10, geographic = true },
    options);

// NEW: Visual anchor verification (requires Azure Spatial Anchors or SLAM)
var anchorService = services.GetRequiredService<ISpatialAnchorService>();
var visualAnchor = await anchorService.CreateAnchorAsync(
    objectId: knowledgeObject.Id,
    gpsCoordinates: new GpsCoordinate(lat, lon),
    visualFeatures: capturedImageFeatures, // from device camera
    expirationDays: 90);

// Proximity verification
var isNearby = await anchorService.VerifyProximityAsync(
    anchorId: visualAnchor.Id,
    currentPosition: deviceGpsPosition,
    maxDistanceMeters: 5.0,
    requireVisualMatch: true);
```

### Pattern 2: Psychometric Content Analysis

**What:** Extract sentiment, emotion, cognitive patterns, and deception signals from text
**When to use:** Content moderation, user profiling, fraud detection, compliance

**Existing implementation (PsychometricIndexingStrategy):**
```csharp
// Source: PsychometricIndexingStrategy.cs (lines 38-89)
var analysis = await psychometricStrategy.AnalyzeAsync(content);
// Returns: sentiment (-1 to 1), emotions (Plutchik/Ekman), writing style, cognitive patterns

// Search by emotion (existing vector store integration)
var emotionalMatches = await psychometricStrategy.SearchByEmotionAsync(
    targetEmotion: "fear",
    minIntensity: 0.7f,
    topK: 20);

// Profile generation from multiple samples
var profile = await psychometricStrategy.GenerateProfileAsync(
    contentSamples: userMessages,
    profileId: userId);
```

**MISSING: Deception detection signals**
```csharp
// Enhancement needed for ADV-02 requirement
public sealed class PsychometricAnalysis
{
    // Existing
    public SentimentScore Sentiment { get; set; }
    public List<EmotionScore> Emotions { get; set; }
    public WritingStyleMetrics WritingStyle { get; set; }

    // ADD for deception detection
    public DeceptionSignals? DeceptionIndicators { get; set; } // NEW
}

public sealed class DeceptionSignals
{
    public float LinguisticDistanceScore { get; init; }      // Unusual word choice
    public float TemporalInconsistencyScore { get; init; }   // Time reference conflicts
    public float EmotionalIncongruenceScore { get; init; }   // Sentiment vs topic mismatch
    public float OverspecificationScore { get; init; }       // Excessive detail (red flag)
    public float HedgingLanguageScore { get; init; }         // "maybe", "possibly", etc.
    public float OverallDeceptionProbability { get; init; }  // Composite 0-1 score
}
```

### Pattern 3: Strategy Integration with Data Management Plugin

**What:** Register strategies with UltimateDataManagement plugin for discovery
**When to use:** All data management strategies follow this pattern

```csharp
// Existing pattern from IndexingStrategyBase
public abstract class IndexingStrategyBase : DataManagementStrategyBase, IIndexingStrategy
{
    public override DataManagementCategory Category => DataManagementCategory.Indexing;
    // Implements: IndexAsync, SearchAsync, RemoveAsync, etc.
}

// NEW: Spatial anchor strategy follows same pattern
public sealed class SpatialAnchorStrategy : IndexingStrategyBase
{
    public override string StrategyId => "index.spatial-anchor-ar";
    public override string DisplayName => "AR Spatial Anchors";
    public override DataManagementCapabilities Capabilities { get; } = new()
    {
        SupportsAsync = true,
        SupportsBatch = false,        // Anchors created one at a time
        SupportsDistributed = true,   // Cloud-backed
        SupportsTransactions = false,
        SupportsTTL = true,           // Anchors can expire
        MaxThroughput = 100,          // Limited by Azure/SLAM processing
        TypicalLatencyMs = 500        // Network + visual processing
    };
}
```

### Anti-Patterns to Avoid

- **Storing visual feature data in traditional indexes:** Visual SLAM features are high-dimensional vectors (not suitable for R-tree). Use specialized vector stores or Azure Spatial Anchors cloud service.
- **GPS-only spatial anchors:** GPS accuracy (3-10m) is insufficient for AR. Always combine with visual features (SLAM) for sub-meter precision.
- **Synchronous psychometric analysis:** NLP/emotion models are expensive (100-800ms). Always use async, batch when possible, cache results.
- **Ignoring AI provider unavailability:** PsychometricIndexingStrategy already has `AIProvider == null` checks with graceful degradation. Maintain this pattern.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| SLAM (Simultaneous Localization And Mapping) | Custom visual feature extraction | Azure Spatial Anchors or ARKit/ARCore via Unity | SLAM requires years of CV research, handle lighting changes, device motion, scale drift |
| Sentiment analysis | Regex-based emotion detection | Azure Text Analytics or pre-trained NLP models | 94 language support, handles sarcasm, context, domain-specific sentiment |
| Deception detection | Rule-based linguistic patterns | Trained transformer models (RoBERTa, BERT) with deception datasets | Research shows 60-70% accuracy for trained models vs 50% for rule-based |
| GeoJSON parsing | Manual JSON parsing for coordinates | GeoJSON.Net library | Handles all geometry types, validation, coordinate systems |
| Vector similarity search | Nested loops for k-NN | Existing vector stores (already in PsychometricIndexingStrategy) | O(n) vs O(log n) for millions of embeddings |

**Key insight:** Both AR anchoring and psychometric analysis are research-heavy domains. Leverage existing cloud services (Azure), proven libraries (GeoJSON.Net), and the existing DataWarehouse infrastructure (vector stores, AI provider abstraction, strategy pattern).

## Common Pitfalls

### Pitfall 1: GPS Precision Illusion
**What goes wrong:** Assuming GPS coordinates provide sub-meter accuracy for AR anchoring
**Why it happens:** Consumer GPS is accurate to 3-10 meters; AR requires centimeter precision
**How to avoid:** Always combine GPS (coarse location) with SLAM visual features (fine location)
**Warning signs:** Users report "anchor is 5 meters away from expected location"

### Pitfall 2: SLAM Requires Stable Visual Features
**What goes wrong:** Anchors fail in dynamic environments (crowds, construction, changing lighting)
**Why it happens:** SLAM relies on persistent visual features (building corners, textures)
**How to avoid:** Store multiple anchor points, validate feature stability, set expiration times
**Warning signs:** Anchor relocation success rate drops below 80%

### Pitfall 3: Psychometric Model Overfitting to Training Data
**What goes wrong:** Emotion detection works in lab but fails on real user content
**Why it happens:** Training datasets (news, reviews) differ from actual use case (chat, documents)
**How to avoid:** Use general-purpose models (Azure Text Analytics), domain-specific fine-tuning when needed
**Warning signs:** Emotion scores cluster around neutral (0.5) for all content

### Pitfall 4: Deception Detection False Positives
**What goes wrong:** Flagging honest users as deceptive based on writing style
**Why it happens:** Many innocent factors mimic deception signals (anxiety, non-native language, cultural differences)
**How to avoid:** Use deception scores as risk indicators (0-1), not binary classification; require human review for high-stakes decisions
**Warning signs:** Deception detection triggers disproportionately for specific user demographics

### Pitfall 5: Spatial Anchor Cloud Dependency
**What goes wrong:** App breaks when Azure Spatial Anchors service is down or unreachable
**Why it happens:** AR anchors stored in cloud, no local fallback
**How to avoid:** Implement fallback to GPS-only mode (existing SpatialIndexStrategy), cache recently accessed anchors locally
**Warning signs:** App fails in offline mode or shows "network error" for spatial queries

### Pitfall 6: Mixing Coordinate Systems
**What goes wrong:** Anchor coordinates don't align between GPS, visual SLAM, and stored data
**Why it happens:** Different coordinate systems (WGS84 for GPS, device-local for SLAM, projected for rendering)
**How to avoid:** Use consistent coordinate system (WGS84 lat/lon for storage), transform at display time
**Warning signs:** Anchors appear at wrong altitude or orientation

## Code Examples

Verified patterns from existing implementations:

### Spatial Indexing (Existing - SpatialIndexStrategy.cs)
```csharp
// Source: SpatialIndexStrategy.cs (lines 636-677)
// Bounding box search using R-tree spatial index
public List<IndexSearchResult> SearchBoundingBox(
    Dictionary<string, string> @params,
    IndexSearchOptions options)
{
    var minX = double.Parse(@params["minx"]);
    var minY = double.Parse(@params["miny"]);
    var maxX = double.Parse(@params["maxx"]);
    var maxY = double.Parse(@params["maxy"]);

    var searchBox = new BoundingBox(minX, minY, maxX, maxY);
    var matchingIds = new List<string>();

    lock (_treeLock)
    {
        SearchTree(_root, searchBox, matchingIds);  // O(log n) R-tree traversal
    }

    return matchingIds
        .Where(id => _documents.ContainsKey(id))
        .Take(options.MaxResults)
        .Select(id => new IndexSearchResult
        {
            ObjectId = id,
            Score = 1.0,
            Snippet = _documents[id].GeoJson,
            Metadata = _documents[id].Metadata
        })
        .ToList();
}
```

### K-Nearest Neighbor with Haversine Distance (Existing)
```csharp
// Source: SpatialIndexStrategy.cs (lines 679-735)
// Geographic distance calculation for real-world coordinates
public List<IndexSearchResult> SearchNearestNeighbors(
    Dictionary<string, string> @params,
    IndexSearchOptions options)
{
    var lat = double.Parse(@params["lat"]);
    var lon = double.Parse(@params["lon"]);
    var queryPoint = new Point2D(lon, lat);
    var useHaversine = bool.Parse(@params.GetValueOrDefault("geographic", "false"));

    var distances = _documents.Values
        .Select(doc =>
        {
            var center = doc.Geometry.BoundingBox.Center;
            var distance = useHaversine
                ? queryPoint.HaversineDistanceTo(center)  // Great-circle distance in km
                : queryPoint.DistanceTo(center);          // Euclidean distance
            return (doc, distance);
        })
        .OrderBy(x => x.distance)
        .Take(options.MaxResults)
        .ToList();

    // Normalize scores (closer = higher score)
    var maxDistance = distances.Max(x => x.distance);
    return distances.Select(x => new IndexSearchResult
    {
        ObjectId = x.doc.ObjectId,
        Score = maxDistance > 0 ? 1.0 - (x.distance / maxDistance) : 1.0,
        Metadata = new Dictionary<string, object> { ["_distance"] = x.distance }
    }).ToList();
}
```

### Psychometric Analysis with AI Provider (Existing)
```csharp
// Source: PsychometricIndexingStrategy.cs (lines 38-89)
public async Task<PsychometricAnalysis> AnalyzeAsync(
    string content,
    CancellationToken ct = default)
{
    if (AIProvider == null)
        throw new InvalidOperationException("AI provider required");

    var depth = GetConfig("AnalysisDepth") ?? "standard";
    var emotionModel = GetConfig("EmotionModel") ?? "plutchik";

    var prompt = BuildAnalysisPrompt(content, depth, emotionModel, enablePersonality);
    var response = await AIProvider.CompleteAsync(new AIRequest
    {
        Prompt = prompt,
        MaxTokens = 800,
        Temperature = 0.3f  // Low temperature for consistent analysis
    }, ct);

    var analysis = ParsePsychometricAnalysis(response.Content);

    // Store in vector store for emotion-based search
    if (VectorStore != null)
    {
        var embedding = await AIProvider.GetEmbeddingsAsync(content, ct);
        await VectorStore.StoreAsync(
            $"psychometric-{Guid.NewGuid():N}",
            embedding,
            metadata: new Dictionary<string, object>
            {
                ["sentiment"] = analysis.Sentiment.Label,
                ["sentiment_score"] = analysis.Sentiment.Score,
                ["dominant_emotion"] = analysis.Emotions
                    .OrderByDescending(e => e.Intensity)
                    .FirstOrDefault()?.Name ?? "neutral"
            },
            ct);
    }

    return analysis;
}
```

### Emotion-Based Vector Search (Existing)
```csharp
// Source: PsychometricIndexingStrategy.cs (lines 110-147)
public async Task<IEnumerable<PsychometricSearchResult>> SearchByEmotionAsync(
    string targetEmotion,
    float minIntensity = 0.5f,
    int topK = 10,
    CancellationToken ct = default)
{
    // Create semantic query embedding for target emotion
    var emotionQuery = $"Content expressing {targetEmotion} emotion with high intensity";
    var queryEmbedding = await AIProvider.GetEmbeddingsAsync(emotionQuery, ct);

    // Vector similarity search with metadata filter
    var matches = await VectorStore.SearchAsync(
        queryEmbedding,
        topK * 2,  // Fetch extra for filtering
        minScore: 0.5f,
        filters: new Dictionary<string, object> { ["dominant_emotion"] = targetEmotion },
        ct);

    return matches
        .Take(topK)
        .Select(m => new PsychometricSearchResult
        {
            DocumentId = m.Entry.Id,
            Score = m.Score,
            Emotion = targetEmotion,
            Intensity = (float)m.Entry.Metadata["sentiment_score"]
        });
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| GPS-only location anchoring | GPS + SLAM visual features | 2018 (ARKit/ARCore launch) | Precision improved from 3-10m to <0.1m |
| Rule-based sentiment (positive/negative word counting) | Transformer models (BERT, RoBERTa) | 2019 (BERT release) | Accuracy improved from 60% to 85%+ with context |
| Device-local spatial anchors | Cloud-persisted anchors (Azure Spatial Anchors) | 2019 | Multi-user, multi-session anchor sharing |
| Single emotion detection | Multi-emotion with intensity scores | 2020+ | Plutchik's 8 emotions vs binary positive/negative |
| Manual deception detection (human review) | AI-assisted deception scoring | 2024-2025 (recent research) | 60-70% automated pre-screening accuracy |

**Deprecated/outdated:**
- **Marker-based AR (QR codes, fiducial markers):** Replaced by markerless SLAM for natural environments
- **VADER/TextBlob sentiment analysis:** Still used for simple cases, but Azure Text Analytics provides better accuracy and language support
- **GPS-only proximity triggers (geofencing):** Insufficient precision for AR; use GPS + visual verification
- **Local-only SLAM (ARKit/ARCore without cloud):** Limits multi-user scenarios; cloud anchors enable persistence and sharing

## Open Questions

1. **Azure Spatial Anchors licensing and cost**
   - What we know: Azure service requires subscription, per-anchor storage costs
   - What's unclear: Cost implications for millions of anchors, data residency requirements
   - Recommendation: Evaluate cost model, consider hybrid (cloud for popular anchors, local for private/temporary)

2. **SLAM performance on low-end mobile devices**
   - What we know: SLAM requires significant CPU/GPU, affects battery life
   - What's unclear: Minimum device specs for acceptable performance
   - Recommendation: Implement device capability detection, graceful degradation to GPS-only mode

3. **Deception detection model accuracy for domain-specific content**
   - What we know: General deception models achieve 60-70% accuracy on forensic/interview data
   - What's unclear: Accuracy on DataWarehouse use cases (document metadata, audit logs, user content)
   - Recommendation: Start with general model, collect ground truth data, fine-tune if needed

4. **Multi-language psychometric analysis**
   - What we know: Azure Text Analytics supports 94 languages
   - What's unclear: Emotion model accuracy across languages (Plutchik/Ekman culturally Western)
   - Recommendation: Validate emotion detection on target languages, consider culture-specific models

5. **Spatial anchor persistence strategy**
   - What we know: Anchors can be stored in cloud (Azure) or local database
   - What's unclear: Optimal TTL, expiration policy, storage capacity limits
   - Recommendation: Default 90-day expiration (configurable), LRU eviction for local cache, cloud for high-value anchors

## Sources

### Primary (HIGH confidence)

- **Existing implementations:**
  - `SpatialIndexStrategy.cs` - R-tree geospatial indexing, GeoJSON, GPS coordinates, k-NN search, Haversine distance
  - `PsychometricIndexingStrategy.cs` - Sentiment analysis, emotion detection (Plutchik/Ekman), personality inference, vector search
  - `IndexingStrategyBase.cs` - Strategy pattern, capabilities, metadata
  - `UltimateDataManagementPlugin.cs` - Plugin orchestration, strategy registry

- **Project documentation:**
  - `.planning/ROADMAP.md` - Phase 11 goals: AR spatial anchors with SLAM/proximity, psychometric indexing with deception detection
  - `.planning/REQUIREMENTS.md` - ADV-01 (spatial anchors), ADV-02 (psychometric + deception)
  - `Metadata/TODO.md` - T87 (spatial AR), T88 (psychometric)

### Secondary (MEDIUM confidence)

- **Azure Spatial Anchors documentation** - [Microsoft Learn](https://learn.microsoft.com/en-us/azure/spatial-anchors/)
  - Platform support: Unity, ARKit, ARCore, UWP, .NET Standard 2.0
  - Authentication: ID/Key based
  - NuGet packages available: Microsoft.Azure.SpatialAnchors.*

- **Azure Text Analytics for .NET** - [Azure SDK for .NET](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.textanalytics-readme)
  - Sentiment analysis: Positive/Negative/Neutral/Mixed with confidence scores
  - Opinion mining: Sentence-level sentiment analysis
  - Language support: 94 languages
  - NuGet: Azure.AI.TextAnalytics 5.x+

- **AR spatial anchors research** - [MDPI Sensors](https://www.mdpi.com/1424-8220/24/4/1161)
  - VSLAM + GPS + Google Street View for outdoor anchor precision enhancement
  - GPS accuracy: 3-10 meters (insufficient for AR)
  - SLAM precision: <0.1 meters with stable visual features

- **Psychometric NLP framework** - [Frontiers in AI](https://www.frontiersin.org/journals/artificial-intelligence/articles/10.3389/frai.2025.1669542/full)
  - Deception detection using linguistic features, emotion features, body language
  - RoBERTa-based emotion extraction, XGBoost classifier for deception
  - Accuracy: 60-70% for automated deception screening

### Tertiary (LOW confidence - needs verification)

- **AR spatial anchor frameworks** - General knowledge from search results
  - Unity AR Foundation: Cross-platform AR (ARKit/ARCore)
  - SLAM algorithms: ORB-SLAM, LSD-SLAM (research prototypes, not production-ready)
  - Recommendation: Use Azure Spatial Anchors for production, avoid custom SLAM

- **Sentiment analysis libraries** - Community recommendations
  - ML.NET TextAnalytics: Local .NET sentiment models (lower accuracy than cloud)
  - Accord.NET: Statistical ML (older, less active)
  - Recommendation: Prefer Azure Text Analytics for production, ML.NET for offline scenarios

## Metadata

**Confidence breakdown:**
- Standard stack: MEDIUM - Azure services well-documented, but .NET implementations sparse for AR
- Architecture: HIGH - Existing strategies provide clear implementation patterns
- Pitfalls: HIGH - Based on known AR/NLP challenges and existing code patterns
- Deception detection: LOW - Recent research area, no production .NET libraries found

**Research date:** 2026-02-11
**Valid until:** 60 days (stable Azure APIs, but AR/NLP research fast-moving)

**Implementation gaps:**
1. **T87 (Spatial AR Anchors):** Need to add AR visual anchoring, SLAM integration, proximity verification to existing SpatialIndexStrategy or create new SpatialAnchorStrategy
2. **T88 (Psychometric Indexing):** Need to add explicit deception detection signals (linguistic distance, temporal inconsistency, emotional incongruence) to existing PsychometricIndexingStrategy

**Dependencies verified:**
- Phase 4 complete: UltimateStorage for spatial/psychometric data persistence ✓
- Phase 2 complete: UniversalIntelligence provides AI provider abstraction ✓
- SDK contracts: Strategy pattern, DataManagementCategory, IIndexingStrategy interfaces exist ✓

---

## Sources

- [AR Spatial Anchors: The Invisible Framework - inAirSpace](https://inairspace.com/blogs/learn-with-inair/ar-spatial-anchors-the-invisible-framework-powering-the-pervasive-future-of-augmented-reality)
- [Enhancement of Outdoor AR Anchor Precision through VSLAM - MDPI](https://www.mdpi.com/1424-8220/24/4/1161)
- [Basics of AR: SLAM - andreasjakl.com](https://www.andreasjakl.com/basics-of-ar-slam-simultaneous-localization-and-mapping/)
- [Azure Spatial Anchors Documentation - Microsoft Learn](https://learn.microsoft.com/en-us/azure/spatial-anchors/)
- [Azure Spatial Anchors NuGet Packages](https://www.nuget.org/profiles/AzureSpatialAnchors)
- [Psycholinguistic NLP Framework for Deception Detection - Frontiers in AI](https://www.frontiersin.org/journals/artificial-intelligence/articles/10.3389/frai.2025.1669542/full)
- [Deception Detection with Emotion Features - Nature Scientific Reports](https://www.nature.com/articles/s41598-025-17741-4)
- [Azure Cognitive Services Text Analytics - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/ai.textanalytics-readme)
- [Sentiment Analysis with Azure AI - Matt on ML.NET](https://accessibleai.dev/post/sentiment-analysis-with-azure-cognitive-services/)
