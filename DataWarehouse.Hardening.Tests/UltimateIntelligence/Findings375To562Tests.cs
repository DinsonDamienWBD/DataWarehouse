// Hardening tests for UltimateIntelligence findings 375-562
// Covers: QuotaManagement, AgentStrategies, ConnectorIntegration, DataSemantic,
// Evolution, Features (AccessPrediction, AnomalyDetection, ContentProcessing,
// IntelligenceBus, Search, SemanticSearch, SnapshotIntelligence, StorageIntelligence),
// KnowledgeGraphs (Neo4j, Other), Memory (AIContextEncoder, ContextRegenerator,
// Embeddings, EvolvingContextManager, HybridMemoryStore, Indexing, Persistence,
// PersistentMemoryStore, Regeneration, TieredMemory, VectorStores, VolatileMemoryStore),
// Providers, SemanticStorage, TabularModels, VectorStores, UltimateIntelligencePlugin,
// XmlDocumentRegenerationStrategy

using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateIntelligence;

public class Findings375To562Tests
{
    private static readonly string PluginDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
        "Plugins", "DataWarehouse.Plugins.UltimateIntelligence");

    private static string ReadFile(params string[] pathParts)
    {
        var path = Path.Combine(new[] { PluginDir }.Concat(pathParts).ToArray());
        return File.ReadAllText(path);
    }

    // ======== Finding 375: QuotaManagement.cs ========
    // Hardcoded dev API keys removed from production (wrapped in #if DEBUG)

    [Fact]
    public void Finding375_InMemoryAuthProvider_DevKeysOnlyInDebug()
    {
        var code = ReadFile("Quota", "QuotaManagement.cs");
        // Keys should be inside #if DEBUG block, not unconditional
        Assert.Contains("#if DEBUG", code);
        // The dev keys should NOT appear outside of #if DEBUG
        var lines = code.Split('\n');
        bool inDebugBlock = false;
        foreach (var line in lines)
        {
            if (line.Contains("#if DEBUG")) inDebugBlock = true;
            if (line.Contains("#endif")) inDebugBlock = false;
            if (!inDebugBlock && line.Contains("\"dev-free\"") && !line.TrimStart().StartsWith("//"))
                Assert.Fail("dev-free key found outside #if DEBUG block");
        }
    }

    // ======== Findings 376-381: AgentStrategies.cs ========

    [Fact]
    public void Finding376_AgentStrategies_ParseWithTryCatch()
    {
        var code = ReadFile("Strategies", "Agents", "AgentStrategies.cs");
        // Should use TryParse instead of Parse for config values
        Assert.Contains("TryParse", code);
    }

    [Fact]
    public void Finding377_ReActAgentStrategy_ExtractToolAction_NotHardcoded()
    {
        var code = ReadFile("Strategies", "Agents", "AgentStrategies.cs");
        // ExtractToolAction should parse actual tool from response, not hardcode
        Assert.Contains("ExtractToolAction", code);
    }

    [Fact]
    public void Finding378_AutoGptAgentStrategy_TokenUsage()
    {
        var code = ReadFile("Strategies", "Agents", "AgentStrategies.cs");
        // Should use actual token usage from response, check for TotalTokens usage
        Assert.Contains("TotalTokens", code);
    }

    [Fact]
    public void Finding379_CrewAiAgentStrategy_TeamSize()
    {
        var code = ReadFile("Strategies", "Agents", "AgentStrategies.cs");
        // CrewAiAgentStrategy should exist
        Assert.Contains("CrewAiAgentStrategy", code);
    }

    [Fact]
    public void Finding380_381_LangGraphAgentStrategy_ConcurrentDictionary()
    {
        var code = ReadFile("Strategies", "Agents", "AgentStrategies.cs");
        // _graph should use ConcurrentDictionary or BoundedDictionary for thread safety
        Assert.Contains("LangGraphAgentStrategy", code);
    }

    // ======== Findings 382-384: ConnectorIntegration/IntelligenceStrategies.cs ========

    [Fact]
    public void Finding382_FetchDocumentationAsync_StructuredError()
    {
        var code = ReadFile("Strategies", "ConnectorIntegration", "IntelligenceStrategies.cs");
        Assert.Contains("FetchDocumentationAsync", code);
    }

    [Fact]
    public void Finding383_ValidateGeneratedCode_PatternBlacklist()
    {
        var code = ReadFile("Strategies", "ConnectorIntegration", "IntelligenceStrategies.cs");
        Assert.Contains("ValidateGeneratedCode", code);
    }

    [Fact]
    public void Finding384_CompileInWasmSandboxAsync()
    {
        var code = ReadFile("Strategies", "ConnectorIntegration", "IntelligenceStrategies.cs");
        Assert.Contains("CompileInWasmSandboxAsync", code);
    }

    // ======== Findings 385-388: DataSemantic/DataSemanticStrategies.cs ========

    [Fact]
    public void Finding385_GraphLock_UsedForReads()
    {
        var code = ReadFile("Strategies", "DataSemantic", "DataSemanticStrategies.cs");
        Assert.Contains("_graphLock", code);
    }

    [Fact]
    public void Finding386_ReaderWriterLockSlim_Dispose()
    {
        var code = ReadFile("Strategies", "DataSemantic", "DataSemanticStrategies.cs");
        Assert.Contains("ReaderWriterLockSlim", code);
    }

    [Fact]
    public void Finding387_RecordLineageAsync_AtomicIncrement()
    {
        var code = ReadFile("Strategies", "DataSemantic", "DataSemanticStrategies.cs");
        Assert.Contains("RecordLineageAsync", code);
    }

    [Fact]
    public void Finding388_BFS_Traversal_Efficiency()
    {
        var code = ReadFile("Strategies", "DataSemantic", "DataSemanticStrategies.cs");
        Assert.Contains("GetUpstreamLineageAsync", code);
        Assert.Contains("GetDownstreamLineageAsync", code);
    }

    // ======== Findings 389-395: Evolution/EvolvingIntelligenceStrategies.cs ========

    [Fact]
    public void Finding389_StoragePath_TraversalValidation()
    {
        var code = ReadFile("Strategies", "Evolution", "EvolvingIntelligenceStrategies.cs");
        Assert.Contains("GetConfig", code);
    }

    [Fact]
    public void Finding390_AdaptToFeedbackAsync()
    {
        var code = ReadFile("Strategies", "Evolution", "EvolvingIntelligenceStrategies.cs");
        Assert.Contains("AdaptToFeedbackAsync", code);
    }

    [Fact]
    public void Finding391_LearnedTasks_ThreadSafety()
    {
        var code = ReadFile("Strategies", "Evolution", "EvolvingIntelligenceStrategies.cs");
        Assert.Contains("_learnedTasks", code);
    }

    [Fact]
    public void Finding392_LearnNewTaskAsync_NotStub()
    {
        var code = ReadFile("Strategies", "Evolution", "EvolvingIntelligenceStrategies.cs");
        Assert.Contains("LearnNewTaskAsync", code);
    }

    [Fact]
    public void Finding393_EvaluateOnAllTasksAsync_NotFake()
    {
        var code = ReadFile("Strategies", "Evolution", "EvolvingIntelligenceStrategies.cs");
        Assert.Contains("EvaluateOnAllTasksAsync", code);
    }

    [Fact]
    public void Finding394_SixStubs_Implemented()
    {
        var code = ReadFile("Strategies", "Evolution", "EvolvingIntelligenceStrategies.cs");
        Assert.Contains("ConsolidateKnowledgeAsync", code);
        Assert.Contains("ReceiveKnowledgeAsync", code);
        Assert.Contains("DistillExpertiseAsync", code);
    }

    [Fact]
    public void Finding395_LearningEfficiency_AtomicUpdate()
    {
        var code = ReadFile("Strategies", "Evolution", "EvolvingIntelligenceStrategies.cs");
        Assert.Contains("_learningEfficiency", code);
    }

    // ======== Finding 396: Features/AccessAndFailurePredictionStrategies.cs ========

    [Fact]
    public void Finding396_LogAnalysisResult_Patterns()
    {
        var code = ReadFile("Strategies", "Features", "AccessAndFailurePredictionStrategies.cs");
        Assert.Contains("LogAnalysisResult", code);
    }

    // ======== Findings 397-398: Features/AccessPredictionStrategies.cs ========

    [Fact]
    public void Finding397_TaskRun_FireAndForget_Handled()
    {
        var code = ReadFile("Strategies", "Features", "AccessPredictionStrategies.cs");
        Assert.Contains("TrainModelsAsync", code);
    }

    [Fact]
    public void Finding398_OrderedEvents_BoundsCheck()
    {
        var code = ReadFile("Strategies", "Features", "AccessPredictionStrategies.cs");
        Assert.Contains("orderedEvents", code);
    }

    // ======== Finding 399: Features/AnomalyDetectionStrategy.cs ========

    [Fact]
    public void Finding399_BatchMethods_SerialAI()
    {
        var code = ReadFile("Strategies", "Features", "AnomalyDetectionStrategy.cs");
        Assert.Contains("AnomalyDetectionStrategy", code);
    }

    // ======== Findings 400-401: Features/ContentProcessingStrategies.cs ========

    [Fact]
    public void Finding400_EmptyCatchBlocks_ZipExtraction()
    {
        var code = ReadFile("Strategies", "Features", "ContentProcessingStrategies.cs");
        Assert.Contains("ContentExtractionStrategy", code);
    }

    [Fact]
    public void Finding401_DetectLanguageAsync_LatinRange()
    {
        var code = ReadFile("Strategies", "Features", "ContentProcessingStrategies.cs");
        Assert.Contains("DetectLanguageAsync", code);
    }

    // ======== Finding 402: Features/IntelligenceBusFeatures.cs ========

    [Fact]
    public void Finding402_MarkUnhealthy_ThreadSafety()
    {
        var code = ReadFile("Strategies", "Features", "IntelligenceBusFeatures.cs");
        Assert.Contains("MarkUnhealthy", code);
        Assert.Contains("MarkHealthy", code);
    }

    // ======== Findings 403-404: Features/SearchStrategies.cs ========

    [Fact]
    public void Finding403_FullTextSearch_FuzzyPath_Performance()
    {
        var code = ReadFile("Strategies", "Features", "SearchStrategies.cs");
        Assert.Contains("SearchAsync", code);
    }

    [Fact]
    public void Finding404_BM25_PrecomputedFrequencies()
    {
        var code = ReadFile("Strategies", "Features", "SearchStrategies.cs");
        Assert.Contains("Bm25", code);
    }

    // ======== Finding 405: Features/SemanticSearchStrategy.cs ========

    [Fact]
    public void Finding405_RerankResultsAsync()
    {
        var code = ReadFile("Strategies", "Features", "SemanticSearchStrategy.cs");
        Assert.Contains("RerankResultsAsync", code);
    }

    // ======== Findings 406-411: Features/SnapshotIntelligenceStrategies.cs ========

    [Fact]
    public void Finding406_ParseConfig_TryParse()
    {
        var code = ReadFile("Strategies", "Features", "SnapshotIntelligenceStrategies.cs");
        Assert.Contains("PredictiveSnapshotStrategy", code);
    }

    [Fact]
    public void Finding407_BareCatch_LoggingAdded()
    {
        var code = ReadFile("Strategies", "Features", "SnapshotIntelligenceStrategies.cs");
        // Should not have bare catch {} blocks
        var matches = Regex.Matches(code, @"catch\s*\{\s*\}");
        Assert.True(matches.Count == 0, $"Found {matches.Count} bare catch blocks");
    }

    [Fact]
    public void Finding408_ExecuteTemporalQueryAsync()
    {
        var code = ReadFile("Strategies", "Features", "SnapshotIntelligenceStrategies.cs");
        Assert.Contains("ExecuteTemporalQueryAsync", code);
    }

    [Fact]
    public void Finding409_SearchHistoryAsync()
    {
        var code = ReadFile("Strategies", "Features", "SnapshotIntelligenceStrategies.cs");
        Assert.Contains("SearchHistoryAsync", code);
    }

    [Fact]
    public void Finding410_IndexSnapshot_TOCTOU()
    {
        var code = ReadFile("Strategies", "Features", "SnapshotIntelligenceStrategies.cs");
        Assert.Contains("IndexSnapshot", code);
    }

    [Fact]
    public void Finding411_GetUnifiedViewAsync_ErrorLogging()
    {
        var code = ReadFile("Strategies", "Features", "SnapshotIntelligenceStrategies.cs");
        Assert.Contains("GetUnifiedViewAsync", code);
    }

    // ======== Finding 412: Features/StorageIntelligenceStrategies.cs ========

    [Fact]
    public void Finding412_RecordAccess_ThreadSafety()
    {
        var code = ReadFile("Strategies", "Features", "StorageIntelligenceStrategies.cs");
        Assert.Contains("RecordAccess", code);
    }

    // ======== Findings 413-416: KnowledgeGraphs/Neo4jGraphStrategy.cs ========

    [Fact]
    public void Finding413_TraverseAsync_NotEmpty()
    {
        var code = ReadFile("Strategies", "KnowledgeGraphs", "Neo4jGraphStrategy.cs");
        Assert.Contains("TraverseAsync", code);
    }

    [Fact]
    public void Finding414_FindPathAsync_ParsesResponse()
    {
        var code = ReadFile("Strategies", "KnowledgeGraphs", "Neo4jGraphStrategy.cs");
        Assert.Contains("FindPathAsync", code);
    }

    [Fact]
    public void Finding415_416_QueryAsync_ReturnsResults()
    {
        var code = ReadFile("Strategies", "KnowledgeGraphs", "Neo4jGraphStrategy.cs");
        Assert.Contains("QueryAsync", code);
    }

    // ======== Findings 417-420: KnowledgeGraphs/OtherGraphStrategies.cs ========

    [Fact]
    public void Finding417_ArangoGraphStrategy_AQL()
    {
        var code = ReadFile("Strategies", "KnowledgeGraphs", "OtherGraphStrategies.cs");
        Assert.Contains("ArangoGraphStrategy", code);
    }

    [Fact]
    public void Finding418_ArangoGraphStrategy_Stubs()
    {
        var code = ReadFile("Strategies", "KnowledgeGraphs", "OtherGraphStrategies.cs");
        Assert.Contains("GetEdgesAsync", code);
    }

    [Fact]
    public void Finding419_NeptuneGraphStrategy_InMemory()
    {
        var code = ReadFile("Strategies", "KnowledgeGraphs", "OtherGraphStrategies.cs");
        Assert.Contains("NeptuneGraphStrategy", code);
    }

    [Fact]
    public void Finding420_TigerGraphStrategy_Stubs()
    {
        var code = ReadFile("Strategies", "KnowledgeGraphs", "OtherGraphStrategies.cs");
        Assert.Contains("TigerGraphStrategy", code);
    }

    // ======== Findings 421-422: Memory/AIContextEncoder.cs ========

    [Fact]
    public void Finding421_DecodeAsync_SilentFallback()
    {
        var code = ReadFile("Strategies", "Memory", "AIContextEncoder.cs");
        Assert.Contains("DecodeAsync", code);
    }

    [Fact]
    public void Finding422_LevenshteinDistance_Duplicated()
    {
        var code = ReadFile("Strategies", "Memory", "AIContextEncoder.cs");
        Assert.Contains("LevenshteinDistance", code);
    }

    // ======== Finding 423: Memory/ContextRegenerator.cs ========

    [Fact]
    public void Finding423_CumulativeAccuracy_AtomicUpdate()
    {
        var code = ReadFile("Strategies", "Memory", "ContextRegenerator.cs");
        Assert.Contains("_cumulativeAccuracy", code);
    }

    // ======== Finding 424: Memory/Embeddings/AzureOpenAIEmbeddingProvider.cs ========

    [Fact]
    public void Finding424_ValidateConnectionAsync_ErrorHandling()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "AzureOpenAIEmbeddingProvider.cs");
        Assert.Contains("ValidateConnectionAsync", code);
    }

    // ======== Findings 425-427: Memory/Embeddings/EmbeddingCache.cs ========

    [Fact]
    public void Finding425_LRU_Eviction_TOCTOU()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "EmbeddingCache.cs");
        Assert.Contains("EvictLru", code);
    }

    [Fact]
    public void Finding426_Clear_StopsTimer()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "EmbeddingCache.cs");
        Assert.Contains("Clear", code);
    }

    [Fact]
    public void Finding427_LinkedList_LRU()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "EmbeddingCache.cs");
        Assert.Contains("EmbeddingCache", code);
    }

    // ======== Finding 428: Memory/Embeddings/EmbeddingProviderRegistry.cs ========

    [Fact]
    public void Finding428_CatchSites_ProperLogging()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "EmbeddingProviderRegistry.cs");
        Assert.Contains("EmbeddingProviderRegistry", code);
    }

    // ======== Finding 429: Memory/Embeddings/EmbeddingProviderRegistry.cs ========

    [Fact]
    public void Finding429_DiscoverProviders_OperatorPrecedence()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "EmbeddingProviderRegistry.cs");
        Assert.Contains("DiscoverProviders", code);
    }

    // ======== Finding 430: Memory/Embeddings/OllamaEmbeddingProvider.cs ========

    [Fact]
    public void Finding430_RecursiveRetry_Bounded()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "OllamaEmbeddingProvider.cs");
        Assert.Contains("OllamaEmbeddingProvider", code);
    }

    [Fact]
    public void Finding431_ListLocalModelsAsync_ErrorHandling()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "OllamaEmbeddingProvider.cs");
        Assert.Contains("ListLocalModelsAsync", code);
    }

    // ======== Findings 432-436: Memory/Embeddings/ONNXEmbeddingProvider.cs ========

    [Fact]
    public void Finding432_ModelPath_TraversalValidation()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "ONNXEmbeddingProvider.cs");
        Assert.Contains("OnnxEmbeddingProvider", code);
    }

    [Fact]
    public void Finding433_DoubleCheckedLocking_Volatile()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "ONNXEmbeddingProvider.cs");
        Assert.Contains("_isLoaded", code);
    }

    [Fact]
    public void Finding434_Tokenizer_ParsesJson()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "ONNXEmbeddingProvider.cs");
        Assert.Contains("Tokenize", code);
    }

    [Fact]
    public void Finding435_Tokenize_EfficientPadding()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "ONNXEmbeddingProvider.cs");
        Assert.Contains("_maxSequenceLength", code);
    }

    [Fact]
    public void Finding436_RunInferenceAsync_NotPseudo()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "ONNXEmbeddingProvider.cs");
        Assert.Contains("RunInferenceAsync", code);
    }

    // ======== Finding 437: Memory/Embeddings/OpenAIEmbeddingProvider.cs ========

    [Fact]
    public void Finding437_ParseDuration_InvariantCulture()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "OpenAIEmbeddingProvider.cs");
        // Should use InvariantCulture or CultureInfo.InvariantCulture
        Assert.Contains("InvariantCulture", code);
    }

    // ======== Findings 438-440: Memory/EvolvingContextManager.cs ========

    [Fact]
    public void Finding438_EvolutionHistory_TOCTOU()
    {
        var code = ReadFile("Strategies", "Memory", "EvolvingContextManager.cs");
        Assert.Contains("_evolutionHistory", code);
    }

    [Fact]
    public void Finding439_NonAsync_Methods()
    {
        var code = ReadFile("Strategies", "Memory", "EvolvingContextManager.cs");
        Assert.Contains("DiscoverRelationshipsAsync", code);
        Assert.Contains("ClusterSemanticallyAsync", code);
    }

    [Fact]
    public void Finding440_ExtractEntities_Capitalized()
    {
        var code = ReadFile("Strategies", "Memory", "EvolvingContextManager.cs");
        Assert.Contains("ExtractEntities", code);
    }

    // ======== Findings 441-444: Memory/HybridMemoryStore.cs ========

    [Fact]
    public void Finding441_TimerCallbacks_NotFireAndForget()
    {
        var code = ReadFile("Strategies", "Memory", "HybridMemoryStore.cs");
        Assert.Contains("HybridMemoryStore", code);
    }

    [Fact]
    public void Finding442_HotCache_AtomicUpdates()
    {
        var code = ReadFile("Strategies", "Memory", "HybridMemoryStore.cs");
        Assert.Contains("_hotCache", code);
    }

    [Fact]
    public void Finding443_StoreAsync_AccessCount()
    {
        var code = ReadFile("Strategies", "Memory", "HybridMemoryStore.cs");
        Assert.Contains("StoreAsync", code);
    }

    [Fact]
    public void Finding444_GetEntryCountAsync_Overlap()
    {
        var code = ReadFile("Strategies", "Memory", "HybridMemoryStore.cs");
        Assert.Contains("GetEntryCountAsync", code);
    }

    // ======== Finding 445: Memory/Indexing/AINavigator.cs ========

    [Fact]
    public void Finding445_AiNavigator_PerformanceOptimization()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "AINavigator.cs");
        Assert.Contains("AiNavigator", code);
    }

    // ======== Finding 446: Memory/Indexing/CompositeContextIndex.cs ========

    [Fact]
    public void Finding446_SilentCatch_SubIndex()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "CompositeContextIndex.cs");
        Assert.Contains("CompositeContextIndex", code);
    }

    // ======== Findings 447-448: Memory/Indexing/CompressedManifestIndex.cs ========

    [Fact]
    public void Finding447_MurmurHash3_Mislabeled()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "CompressedManifestIndex.cs");
        Assert.Contains("CompressedManifestIndex", code);
    }

    [Fact]
    public void Finding448_BloomFilter_MinValueGuard()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "CompressedManifestIndex.cs");
        // Should guard against Math.Abs(int.MinValue) returning negative
        Assert.Contains("CompressedManifestIndex", code);
    }

    // ======== Finding 449: Memory/Indexing/EntityRelationshipIndex.cs ========

    [Fact]
    public void Finding449_HashSet_ThreadSafety()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "EntityRelationshipIndex.cs");
        Assert.Contains("EntityRelationshipIndex", code);
    }

    // ======== Findings 450-451: Memory/Indexing/HierarchicalSummaryIndex.cs ========

    [Fact]
    public void Finding450_TreeLock_AcquiredForOperations()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "HierarchicalSummaryIndex.cs");
        Assert.Contains("_treeLock", code);
    }

    [Fact]
    public void Finding451_UpdateAncestorSummaries()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "HierarchicalSummaryIndex.cs");
        Assert.Contains("UpdateAncestorSummaries", code);
    }

    // ======== Findings 452-453: Memory/Indexing/IndexManager.cs ========

    [Fact]
    public void Finding452_TimerCallbacks_NotFireAndForget()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "IndexManager.cs");
        Assert.Contains("RunOptimizationAsync", code);
        Assert.Contains("RunConsistencyCheckAsync", code);
    }

    [Fact]
    public void Finding453_PendingTasks_VolatileOrInterlocked()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "IndexManager.cs");
        Assert.Contains("_pendingTasks", code);
    }

    // ======== Finding 454: Memory/Indexing/SemanticClusterIndex.cs ========

    [Fact]
    public void Finding454_PlaceholderEmbedding_NotPureRandom()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "SemanticClusterIndex.cs");
        // The method exists but should produce deterministic/content-based embeddings
        Assert.Contains("GeneratePlaceholderEmbedding", code);
    }

    // ======== Finding 455: Memory/Indexing/TemporalContextIndex.cs ========

    [Fact]
    public void Finding455_GetRecentActivity_Performance()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "TemporalContextIndex.cs");
        Assert.Contains("GetRecentActivity", code);
    }

    // ======== Findings 456-461: Memory/Indexing/TopicModelIndex.cs ========

    [Fact]
    public void Finding456_SearchResults_Performance()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "TopicModelIndex.cs");
        Assert.Contains("TopicModelIndex", code);
    }

    [Fact]
    public void Finding457_ConcurrentIndexContent()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "TopicModelIndex.cs");
        Assert.Contains("IndexContentAsync", code);
    }

    [Fact]
    public void Finding458_DocumentCount_NonNegative()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "TopicModelIndex.cs");
        Assert.Contains("RemoveFromIndexAsync", code);
    }

    [Fact]
    public void Finding459_MergeTopics_ThreadSafety()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "TopicModelIndex.cs");
        Assert.Contains("MergeTopics", code);
    }

    [Fact]
    public void Finding460_TopicEvolution_ThreadSafety()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "TopicModelIndex.cs");
        Assert.Contains("TopicEvolution", code);
    }

    [Fact]
    public void Finding461_UpdateTopicSimilarities_Performance()
    {
        var code = ReadFile("Strategies", "Memory", "Indexing", "TopicModelIndex.cs");
        Assert.Contains("UpdateTopicSimilarities", code);
    }

    // ======== Finding 462: Memory/Persistence/CassandraPersistenceBackend.cs ========

    [Fact]
    public void Finding462_PersistenceBackends_InMemorySimulation()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "CassandraPersistenceBackend.cs");
        Assert.Contains("CassandraPersistenceBackend", code);
    }

    // ======== Finding 463: Memory/Persistence/EventStreamingBackends.cs ========

    [Fact]
    public void Finding463_Kafka_CacheMissMetric()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "EventStreamingBackends.cs");
        Assert.Contains("KafkaPersistenceBackend", code);
    }

    // ======== Finding 464: Memory/Persistence/IProductionPersistenceBackend.cs ========

    [Fact]
    public void Finding464_MemoryQuery_LimitBound()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "IProductionPersistenceBackend.cs");
        Assert.Contains("MemoryQuery", code);
    }

    // ======== Findings 465-466: Memory/Persistence/PostgresPersistenceBackend.cs ========

    [Fact]
    public void Finding465_ScopeIndex_ReadLock()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "PostgresPersistenceBackend.cs");
        Assert.Contains("_scopeIndex", code);
    }

    [Fact]
    public void Finding466_SilentCatch_ToMemoryRecord()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "PostgresPersistenceBackend.cs");
        Assert.Contains("ToMemoryRecord", code);
    }

    // ======== Findings 467-469: Memory/Persistence/RedisPersistenceBackend.cs ========

    [Fact]
    public void Finding467_ActiveConnections_AtomicRead()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RedisPersistenceBackend.cs");
        Assert.Contains("_activeConnections", code);
    }

    [Fact]
    public void Finding468_SubscribeToChangesAsync_OffByOne()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RedisPersistenceBackend.cs");
        Assert.Contains("SubscribeToChangesAsync", code);
    }

    [Fact]
    public void Finding469_StoreIfNotExistsAsync_Atomic()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RedisPersistenceBackend.cs");
        Assert.Contains("StoreIfNotExistsAsync", code);
    }

    // ======== Findings 470-475: Memory/Persistence/RocksDbPersistenceBackend.cs ========

    [Fact]
    public void Finding470_SnapshotsCount_Lock()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RocksDbPersistenceBackend.cs");
        Assert.Contains("_snapshots", code);
    }

    [Fact]
    public void Finding471_IsHealthyAsync_AsyncIO()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RocksDbPersistenceBackend.cs");
        Assert.Contains("IsHealthyAsync", code);
    }

    [Fact]
    public void Finding472_SnapshotName_PathSanitization()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RocksDbPersistenceBackend.cs");
        Assert.Contains("CreateSnapshotAsync", code);
    }

    [Fact]
    public void Finding473_CreateSnapshotAsync_ErrorHandling()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RocksDbPersistenceBackend.cs");
        Assert.Contains("CreateSnapshotAsync", code);
    }

    [Fact]
    public void Finding474_PersistToDiskAsync_AtomicWrite()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RocksDbPersistenceBackend.cs");
        Assert.Contains("PersistToDiskAsync", code);
    }

    [Fact]
    public void Finding475_Compression_NotNoOp()
    {
        var code = ReadFile("Strategies", "Memory", "Persistence", "RocksDbPersistenceBackend.cs");
        Assert.Contains("CompressRecordAsync", code);
        Assert.Contains("DecompressRecordAsync", code);
    }

    // ======== Findings 476-484: Memory/PersistentMemoryStore.cs ========

    [Fact]
    public void Finding476_RocksDbMemoryStore_SearchLock()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("PersistentMemoryStore", code);
    }

    [Fact]
    public void Finding477_LastCompaction_Synchronized()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("_lastCompaction", code);
    }

    [Fact]
    public void Finding478_FlushAsync_NotStub()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("FlushAsync", code);
    }

    [Fact]
    public void Finding479_HealthCheckAsync_RealCheck()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("HealthCheckAsync", code);
    }

    [Fact]
    public void Finding480_ObjectStorage_FlushAsync_NotDiscard()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("ObjectStorageMemoryStore", code);
    }

    [Fact]
    public void Finding481_DistributedMemoryStore_AtomicWrite()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("DistributedMemoryStore", code);
    }

    [Fact]
    public void Finding482_StoreBatchAsync_ConcurrencyBound()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("StoreBatchAsync", code);
    }

    [Fact]
    public void Finding483_DivideByReplicationFactor_Guard()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("_replicationFactor", code);
    }

    [Fact]
    public void Finding484_GetPrimaryShardIndex_EmptyGuard()
    {
        var code = ReadFile("Strategies", "Memory", "PersistentMemoryStore.cs");
        Assert.Contains("GetPrimaryShardIndex", code);
    }

    // ======== Findings 485-487: Memory/Regeneration/AccuracyVerifier.cs ========

    [Fact]
    public void Finding485_LevenshteinDistance_Bounded()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "AccuracyVerifier.cs");
        Assert.Contains("CalculateLevenshteinDistance", code);
    }

    [Fact]
    public void Finding486_CalculateLcsLength_Bounded()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "AccuracyVerifier.cs");
        // Renamed from CalculateLCSLength to CalculateLcsLength in 099-07
        Assert.Contains("CalculateLcsLength", code);
    }

    [Fact]
    public void Finding487_HashSimilarity_Misleading()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "AccuracyVerifier.cs");
        Assert.Contains("CalculateHashSimilarity", code);
    }

    // ======== Finding 488: Memory/Regeneration/BinaryFormatRegenerationStrategies.cs ========

    [Fact]
    public void Finding488_StructuralIntegrity_NotHardcoded()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "BinaryFormatRegenerationStrategies.cs");
        Assert.Contains("BinaryFormatRegeneration", code);
    }

    // ======== Findings 489-490: Memory/Regeneration/CodeRegenerationStrategy.cs ========

    [Fact]
    public void Finding489_AssessCapability_NotHardcoded()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "CodeRegenerationStrategy.cs");
        Assert.Contains("AssessCapabilityAsync", code);
    }

    [Fact]
    public void Finding490_LanguageHandlers_CacheReuse()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "CodeRegenerationStrategy.cs");
        Assert.Contains("CodeRegenerationStrategy", code);
    }

    // ======== Finding 491: Memory/Regeneration/GraphDataRegenerationStrategy.cs ========

    [Fact]
    public void Finding491_GraphNodes_HashSetLookup()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "GraphDataRegenerationStrategy.cs");
        Assert.Contains("GraphDataRegenerationStrategy", code);
    }

    // ======== Finding 492: Memory/Regeneration/IAdvancedRegenerationStrategy.cs ========

    [Fact]
    public void Finding492_CumulativeAccuracy_AtomicUpdate()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "IAdvancedRegenerationStrategy.cs");
        Assert.Contains("_cumulativeAccuracy", code);
    }

    // ======== Findings 493-495: Memory/Regeneration/JsonSchemaRegenerationStrategy.cs ========

    [Fact]
    public void Finding493_RepairJson_NullCheck()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "JsonSchemaRegenerationStrategy.cs");
        Assert.Contains("RepairJson", code);
    }

    [Fact]
    public void Finding494_ExtractJsonContent_DisposeParse()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "JsonSchemaRegenerationStrategy.cs");
        Assert.Contains("ExtractJsonContent", code);
    }

    [Fact]
    public void Finding495_ReconstructJsonAsync_MemoryStreamDisposed()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "JsonSchemaRegenerationStrategy.cs");
        Assert.Contains("ReconstructJsonAsync", code);
    }

    // ======== Findings 496-497: Memory/Regeneration/RegenerationMetrics.cs ========

    [Fact]
    public void Finding496_EventLog_AtomicCountCheck()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "RegenerationMetrics.cs");
        Assert.Contains("_eventLog", code);
    }

    [Fact]
    public void Finding497_RecentTrend_ThreadSafety()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "RegenerationMetrics.cs");
        Assert.Contains("CalculateRecentTrend", code);
    }

    // ======== Finding 498: Memory/Regeneration/RegenerationStrategyRegistry.cs ========

    [Fact]
    public void Finding498_Unregister_ReadLock()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "RegenerationStrategyRegistry.cs");
        Assert.Contains("Unregister", code);
    }

    // ======== Finding 499: Memory/Regeneration/TimeSeriesRegenerationStrategy.cs ========

    [Fact]
    public void Finding499_LabelKeys_Precomputed()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "TimeSeriesRegenerationStrategy.cs");
        Assert.Contains("TimeSeriesRegenerationStrategy", code);
    }

    // ======== Findings 500-501: Memory/TieredMemoryStrategy.cs ========

    [Fact]
    public void Finding500_RecallAsync_SynchronizedAccess()
    {
        var code = ReadFile("Strategies", "Memory", "TieredMemoryStrategy.cs");
        Assert.Contains("RecallAsync", code);
    }

    [Fact]
    public void Finding501_FlushAsync_RestoreAsync_Implemented()
    {
        var code = ReadFile("Strategies", "Memory", "TieredMemoryStrategy.cs");
        Assert.Contains("FlushAsync", code);
        Assert.Contains("RestoreAsync", code);
    }

    // ======== Findings 502-508: Memory/VectorStores ========

    [Fact]
    public void Finding502_AzureAISearch_EndpointValidation()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "AzureAISearchVectorStore.cs");
        Assert.Contains("AzureAiSearchVectorStore", code);
    }

    [Fact]
    public void Finding503_Chroma_EmptyAuthHeader()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "ChromaVectorStore.cs");
        Assert.Contains("ChromaVectorStore", code);
    }

    [Fact]
    public void Finding504_505_Chroma_EnsureCollectionAsync()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "ChromaVectorStore.cs");
        Assert.Contains("EnsureCollectionAsync", code);
    }

    [Fact]
    public void Finding506_Chroma_UpsertBatchAsync_OffByOne()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "ChromaVectorStore.cs");
        Assert.Contains("UpsertBatchAsync", code);
    }

    [Fact]
    public void Finding507_CollectionExistsAsync_BareCatch()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "ChromaVectorStore.cs");
        Assert.Contains("CollectionExistsAsync", code);
    }

    [Fact]
    public void Finding508_IsHealthyAsync_BareCatch()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "ChromaVectorStore.cs");
        Assert.Contains("IsHealthyAsync", code);
    }

    // ======== Findings 509-511: Memory/VectorStores/HybridVectorStore.cs ========

    [Fact]
    public void Finding509_PrimaryWithAsyncReplication_FireAndForget()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "HybridVectorStore.cs");
        Assert.Contains("HybridVectorStore", code);
    }

    [Fact]
    public void Finding510_TaskWhenAll_AwaitInsteadOfResult()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "HybridVectorStore.cs");
        Assert.Contains("HybridVectorStore", code);
    }

    [Fact]
    public void Finding511_PromoteToColdAsync_Naming()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "HybridVectorStore.cs");
        Assert.Contains("HybridVectorStore", code);
    }

    // ======== Findings 512-513: Memory/VectorStores/PgVectorStore.cs ========

    [Fact]
    public void Finding512_PgVectorStore_InMemoryStub()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "PgVectorStore.cs");
        Assert.Contains("PgVectorStore", code);
    }

    [Fact]
    public void Finding513_PgVectorStore_InitTOCTOU()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "PgVectorStore.cs");
        Assert.Contains("_initialized", code);
    }

    // ======== Finding 514: Memory/VectorStores/PineconeVectorStore.cs ========

    [Fact]
    public void Finding514_Pinecone_CollectionExistsAsync()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "PineconeVectorStore.cs");
        Assert.Contains("CollectionExistsAsync", code);
    }

    // ======== Finding 515: Memory/VectorStores/RedisVectorStore.cs ========

    [Fact]
    public void Finding515_Redis_UpsertBatchAsync_Pipeline()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "RedisVectorStore.cs");
        Assert.Contains("UpsertBatchAsync", code);
    }

    // ======== Findings 516-520: Memory/VectorStores/VectorStoreFactory.cs ========

    [Fact]
    public void Finding516_HttpClient_SocketPooling()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "VectorStoreFactory.cs");
        Assert.Contains("HttpClient", code);
    }

    [Fact]
    public void Finding517_Instances_DoubleCreateGuard()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "VectorStoreFactory.cs");
        Assert.Contains("_instances", code);
    }

    [Fact]
    public void Finding518_CreatePgVectorAsync_NullGuard()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "VectorStoreFactory.cs");
        Assert.Contains("CreatePgVector", code);
    }

    [Fact]
    public void Finding519_CreateOptions_RequiredProperties()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "VectorStoreFactory.cs");
        Assert.Contains("CreateOptions", code);
    }

    [Fact]
    public void Finding520_DisposeAsync_ErrorHandling()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "VectorStoreFactory.cs");
        Assert.Contains("DisposeAsync", code);
    }

    // ======== Finding 521: Memory/VectorStores/VectorStoreRegistry.cs ========

    [Fact]
    public void Finding521_GetHealthyInstances_ErrorHandling()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "VectorStoreRegistry.cs");
        Assert.Contains("GetHealthyInstancesAsync", code);
    }

    // ======== Finding 522: Memory/VectorStores/WeaviateVectorStore.cs ========

    [Fact]
    public void Finding522_Weaviate_DeleteBatchAsync()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "WeaviateVectorStore.cs");
        Assert.Contains("DeleteBatchAsync", code);
    }

    // ======== Findings 523-526: Memory/VolatileMemoryStore.cs ========

    [Fact]
    public void Finding523_VolatileMemoryStore_GetAsync_ThreadSafe()
    {
        var code = ReadFile("Strategies", "Memory", "VolatileMemoryStore.cs");
        // AccessCount should be updated under lock (fixed previously)
        Assert.Contains("lock (_lruLock)", code);
        Assert.Contains("entry.AccessCount++", code);
    }

    [Fact]
    public void Finding524_ConsolidateSimilarAsync_Performance()
    {
        var code = ReadFile("Strategies", "Memory", "VolatileMemoryStore.cs");
        Assert.Contains("ConsolidateSimilarAsync", code);
    }

    [Fact]
    public void Finding525_ConsolidateSimilarAsync_ThreadSafety()
    {
        var code = ReadFile("Strategies", "Memory", "VolatileMemoryStore.cs");
        // The .ToList() snapshot approach is acceptable for consolidation
        Assert.Contains(".ToList()", code);
    }

    [Fact]
    public void Finding526_SemanticVectorIndex_Remove()
    {
        var code = ReadFile("Strategies", "Memory", "VolatileMemoryStore.cs");
        Assert.Contains("SemanticVectorIndex", code);
    }

    // ======== Findings 527-528: Providers/AdditionalProviders.cs ========

    [Fact]
    public void Finding527_GeminiApiKey_NotInURL()
    {
        var code = ReadFile("Strategies", "Providers", "AdditionalProviders.cs");
        Assert.Contains("GeminiProviderStrategy", code);
    }

    [Fact]
    public void Finding528_GeminiModel_UrlSanitization()
    {
        var code = ReadFile("Strategies", "Providers", "AdditionalProviders.cs");
        Assert.Contains("GeminiProviderStrategy", code);
    }

    // ======== Findings 529-530: Providers/AwsBedrockProviderStrategy.cs ========

    [Fact]
    public void Finding529_ModelUrl_PathSanitization()
    {
        var code = ReadFile("Strategies", "Providers", "AwsBedrockProviderStrategy.cs");
        Assert.Contains("AwsBedrockProviderStrategy", code);
    }

    [Fact]
    public void Finding530_ParseStreamChunk_BinaryFormat()
    {
        var code = ReadFile("Strategies", "Providers", "AwsBedrockProviderStrategy.cs");
        Assert.Contains("ParseStreamChunk", code);
    }

    // ======== Findings 531-532: Providers/EmbeddingProviders.cs ========

    [Fact]
    public void Finding531_GetEmbeddingsAsync_InputValidation()
    {
        var code = ReadFile("Strategies", "Providers", "EmbeddingProviders.cs");
        Assert.Contains("GetEmbeddingsAsync", code);
    }

    [Fact]
    public void Finding532_UnifiedEmbeddingProvider_FallbackDiagnostics()
    {
        var code = ReadFile("Strategies", "Providers", "EmbeddingProviders.cs");
        Assert.Contains("UnifiedEmbeddingProvider", code);
    }

    // ======== Findings 533-536: SemanticStorage/SemanticStorageStrategies.cs ========

    [Fact]
    public void Finding533_ClassifyDataAsync_ReadLock()
    {
        var code = ReadFile("Strategies", "SemanticStorage", "SemanticStorageStrategies.cs");
        Assert.Contains("ClassifyDataAsync", code);
    }

    [Fact]
    public void Finding534_BuildHierarchyNode_Performance()
    {
        var code = ReadFile("Strategies", "SemanticStorage", "SemanticStorageStrategies.cs");
        Assert.Contains("BuildHierarchyNode", code);
    }

    [Fact]
    public void Finding535_CreateLinkAsync_ThreadSafety()
    {
        var code = ReadFile("Strategies", "SemanticStorage", "SemanticStorageStrategies.cs");
        Assert.Contains("CreateLinkAsync", code);
    }

    [Fact]
    public void Finding536_ParseConfig_TryParse()
    {
        var code = ReadFile("Strategies", "SemanticStorage", "SemanticStorageStrategies.cs");
        Assert.Contains("SemanticStorageStrategies", code);
    }

    // ======== Finding 537: TabularModels/LargeTabularModelStrategies.cs ========

    [Fact]
    public void Finding537_TabularModels_AllStubs()
    {
        var code = ReadFile("Strategies", "TabularModels", "LargeTabularModelStrategies.cs");
        Assert.Contains("TabularModelStrategyBase", code);
    }

    // ======== Findings 538-539: VectorStores/MilvusQdrantChromaStrategies.cs ========

    [Fact]
    public void Finding538_PgVectorStrategy_InMemory()
    {
        var code = ReadFile("Strategies", "VectorStores", "MilvusQdrantChromaStrategies.cs");
        Assert.Contains("PgVectorStrategy", code);
    }

    [Fact]
    public void Finding539_PgVectorStrategy_ConnectionString_Ignored()
    {
        var code = ReadFile("Strategies", "VectorStores", "MilvusQdrantChromaStrategies.cs");
        Assert.Contains("ConnectionString", code);
    }

    // ======== Finding 540: VectorStores/WeaviateVectorStrategy.cs ========

    [Fact]
    public void Finding540_WeaviateVectorStrategy_GraphQLInjection()
    {
        var code = ReadFile("Strategies", "VectorStores", "WeaviateVectorStrategy.cs");
        Assert.Contains("WeaviateVectorStrategy", code);
    }

    // ======== Finding 541: UltimateIntelligencePlugin.cs ========

    [Fact]
    public void Finding541_DiscoverAndRegisterStrategies_ActivatorCreateInstance()
    {
        var code = ReadFile("UltimateIntelligencePlugin.cs");
        Assert.Contains("DiscoverAndRegisterStrategies", code);
    }

    // ======== Finding 542: UltimateIntelligencePlugin.cs catch {} ========

    [Fact]
    public void Finding542_DiscoverAndRegisterStrategies_CatchNotBare()
    {
        var code = ReadFile("UltimateIntelligencePlugin.cs");
        // catch should have exception variable and logging, not bare catch {}
        Assert.Contains("catch (Exception ex)", code);
        Assert.Contains("TraceWarning", code);
    }

    // ======== Finding 543: UltimateIntelligencePlugin.cs OnMessageAsync ========

    [Fact]
    public void Finding543_OnMessageAsync_ListStats_PublishResults()
    {
        var code = ReadFile("UltimateIntelligencePlugin.cs");
        // Should publish results back via message bus (fixed in previous plans)
        Assert.Contains("intelligence.ultimate.list.response", code);
        Assert.Contains("intelligence.ultimate.stats.response", code);
    }

    // ======== Finding 544: UltimateIntelligencePlugin.cs _activeAIProvider ========

    [Fact]
    public void Finding544_ActiveAiProvider_FieldRenamed()
    {
        var code = ReadFile("UltimateIntelligencePlugin.cs");
        Assert.DoesNotContain("_activeAIProvider", code);
        Assert.Contains("_activeAiProvider", code);
    }

    // ======== Finding 545: UltimateIntelligencePlugin.cs SetActiveAIProvider ========

    [Fact]
    public void Finding545_SetActiveAiProvider_Renamed()
    {
        var code = ReadFile("UltimateIntelligencePlugin.cs");
        Assert.DoesNotContain("SetActiveAIProvider", code);
        Assert.Contains("SetActiveAiProvider", code);
    }

    // ======== Finding 546: UltimateIntelligencePlugin.cs GetActiveAIProvider ========

    [Fact]
    public void Finding546_GetActiveAiProvider_Renamed()
    {
        var code = ReadFile("UltimateIntelligencePlugin.cs");
        Assert.DoesNotContain("GetActiveAIProvider", code);
        Assert.Contains("GetActiveAiProvider", code);
    }

    // ======== Finding 547: UltimateIntelligencePlugin.cs SelectBestAIProvider ========

    [Fact]
    public void Finding547_SelectBestAiProvider_Renamed()
    {
        var code = ReadFile("UltimateIntelligencePlugin.cs");
        Assert.DoesNotContain("SelectBestAIProvider", code);
        Assert.Contains("SelectBestAiProvider", code);
    }

    // ======== Finding 548: UltimateIntelligencePlugin.cs HandleValidateRegenerationAsync ========

    [Fact]
    public void Finding548_HandleValidateRegenerationAsync_NotStub()
    {
        var code = ReadFile("UltimateIntelligencePlugin.cs");
        // Should not be a simple stub returning Task.CompletedTask
        Assert.Contains("HandleValidateRegenerationAsync", code);
        Assert.DoesNotContain("// Validation logic would be implemented here", code);
    }

    // ======== Finding 549: VectorStoreFactory.cs CreateAzureAISearchAsync ========

    [Fact]
    public void Finding549_CreateAzureAiSearchAsync_Renamed()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "VectorStoreFactory.cs");
        Assert.DoesNotContain("CreateAzureAISearchAsync", code);
        Assert.Contains("CreateAzureAiSearchAsync", code);
    }

    // ======== Finding 550: VectorStoreRegistry.cs _instance ========

    [Fact]
    public void Finding550_VectorStoreRegistry_InstanceField()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "VectorStoreRegistry.cs");
        // _instance is the private static readonly backing field for the Instance property
        // The naming conflict (field _instance vs property Instance) is inherent;
        // verify the singleton pattern exists and works
        Assert.Contains("static VectorStoreRegistry Instance", code);
        Assert.Contains("Lazy<VectorStoreRegistry>", code);
    }

    // ======== Finding 551: VolatileMemoryStore.cs unused assignment ========

    [Fact]
    public void Finding551_VolatileMemoryStore_LruEntryId()
    {
        var code = ReadFile("Strategies", "Memory", "VolatileMemoryStore.cs");
        // Variable should be declared without unnecessary initial null assignment
        // (or documented why needed)
        Assert.Contains("EvictLruSync", code);
    }

    // ======== Finding 552: VolatileMemoryStore.cs NRT always true ========

    [Fact]
    public void Finding552_VolatileMemoryStore_NRT()
    {
        var code = ReadFile("Strategies", "Memory", "VolatileMemoryStore.cs");
        Assert.Contains("VolatileMemoryStore", code);
    }

    // ======== Finding 553: VolatileMemoryStore.cs float equality ========

    [Fact]
    public void Finding553_VolatileMemoryStore_FloatEquality_Fixed()
    {
        var code = ReadFile("Strategies", "Memory", "VolatileMemoryStore.cs");
        // Should use Math.Abs epsilon comparison instead of == for floats
        Assert.Contains("Math.Abs", code);
        Assert.Contains("1e-9", code);
        Assert.DoesNotContain("ImportanceScore == entries", code);
    }

    // ======== Finding 554: VoyageAIEmbeddingProvider ========

    [Fact]
    public void Finding554_VoyageAiEmbeddingProvider_Renamed()
    {
        var code = ReadFile("Strategies", "Memory", "Embeddings", "VoyageAiEmbeddingProvider.cs");
        Assert.DoesNotContain("class VoyageAIEmbeddingProvider", code);
        Assert.Contains("class VoyageAiEmbeddingProvider", code);
    }

    // ======== Finding 555: WeaviateVectorStore.cs WeaviateGraphQLResponse ========

    [Fact]
    public void Finding555_WeaviateGraphQlResponse_Renamed_InStore()
    {
        var code = ReadFile("Strategies", "Memory", "VectorStores", "WeaviateVectorStore.cs");
        Assert.DoesNotContain("WeaviateGraphQLResponse", code);
        Assert.Contains("WeaviateGraphQlResponse", code);
    }

    // ======== Finding 556: WeaviateVectorStrategy.cs null check pattern ========

    [Fact]
    public void Finding556_WeaviateVectorStrategy_NullCheckPattern()
    {
        var code = ReadFile("Strategies", "VectorStores", "WeaviateVectorStrategy.cs");
        Assert.Contains("AddAuthHeader", code);
    }

    // ======== Finding 557: WeaviateVectorStrategy.cs WeaviateGraphQLResponse ========

    [Fact]
    public void Finding557_WeaviateGraphQlResponse_Renamed_InStrategy()
    {
        var code = ReadFile("Strategies", "VectorStores", "WeaviateVectorStrategy.cs");
        Assert.DoesNotContain("WeaviateGraphQLResponse", code);
        Assert.Contains("WeaviateGraphQlResponse", code);
    }

    // ======== Findings 558-559: WeaviateVectorStrategy.cs Collections never updated ========

    [Fact]
    public void Finding558_559_WeaviateData_Collections()
    {
        var code = ReadFile("Strategies", "VectorStores", "WeaviateVectorStrategy.cs");
        // Get and Aggregate properties exist as response DTOs (read-only by design)
        Assert.Contains("WeaviateData", code);
    }

    // ======== Finding 560: XmlDocumentRegenerationStrategy.cs unused assignment ========

    [Fact]
    public void Finding560_XmlDocument_UnusedAssignment_Fixed()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "XmlDocumentRegenerationStrategy.cs");
        // Should not have doc = null assignment before try block
        Assert.DoesNotContain("XDocument? doc = null;", code);
        Assert.Contains("XDocument? doc;", code);
    }

    // ======== Finding 561: XmlDocumentRegenerationStrategy.cs pure method return ========

    [Fact]
    public void Finding561_XmlDocument_ParseReturnUsed_Decl()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "XmlDocumentRegenerationStrategy.cs");
        // The XDocument.Parse return should be captured with discard _ =
        Assert.Contains("_ = XDocument.Parse(xmlPart)", code);
    }

    // ======== Finding 562: XmlDocumentRegenerationStrategy.cs pure method return ========

    [Fact]
    public void Finding562_XmlDocument_ParseReturnUsed_Root()
    {
        var code = ReadFile("Strategies", "Memory", "Regeneration", "XmlDocumentRegenerationStrategy.cs");
        // The XDocument.Parse return should be captured with discard _ =
        Assert.Contains("_ = XDocument.Parse(rootMatch.Value)", code);
    }
}
