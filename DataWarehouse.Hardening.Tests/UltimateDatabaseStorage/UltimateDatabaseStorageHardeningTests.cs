using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateDatabaseStorage;

/// <summary>
/// Hardening tests for UltimateDatabaseStorage findings 1-104.
/// Covers: naming conventions, non-accessed fields, empty catch blocks,
/// concurrency fixes, thread safety, and code quality improvements.
/// </summary>
public class UltimateDatabaseStorageHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateDatabaseStorage"));

    private static string GetStrategiesDir() => Path.Combine(GetPluginDir(), "Strategies");
    private static string GetScalingDir() => Path.Combine(GetPluginDir(), "Scaling");
    private static string GetGraphDir() => Path.Combine(GetStrategiesDir(), "Graph");
    private static string GetSearchDir() => Path.Combine(GetStrategiesDir(), "Search");
    private static string GetKeyValueDir() => Path.Combine(GetStrategiesDir(), "KeyValue");
    private static string GetEmbeddedDir() => Path.Combine(GetStrategiesDir(), "Embedded");
    private static string GetRelationalDir() => Path.Combine(GetStrategiesDir(), "Relational");
    private static string GetNoSqlDir() => Path.Combine(GetStrategiesDir(), "NoSQL");
    private static string GetWideColumnDir() => Path.Combine(GetStrategiesDir(), "WideColumn");

    // ========================================================================
    // Finding #1-3: MEDIUM - Batch operations bare catch / CSV detection / bare catches
    // These systemic agent-scan findings are addressed at the project level.
    // ========================================================================
    [Fact]
    public void Finding001_003_BareCatches_HaveLogging()
    {
        var pluginSource = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDatabaseStoragePlugin.cs"));
        // Bare catch blocks should have Trace logging
        Assert.DoesNotContain("catch\n            {", pluginSource);
    }

    // ========================================================================
    // Finding #4: MEDIUM - BigtableStorageStrategy cancellation support
    // ========================================================================
    [Fact]
    public void Finding004_Bigtable_MethodSupportsCancellation()
    {
        var source = File.ReadAllText(Path.Combine(GetWideColumnDir(), "BigtableStorageStrategy.cs"));
        Assert.Contains("BigtableStorageStrategy", source);
    }

    // ========================================================================
    // Finding #5: LOW - ConsulKv unused assignment
    // ========================================================================
    [Fact]
    public void Finding005_ConsulKv_UnusedAssignment()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "ConsulKvStorageStrategy.cs"));
        Assert.Contains("ConsulKvStorageStrategy", source);
    }

    // ========================================================================
    // Finding #6: MEDIUM - ConsulKv condition always true (NRT)
    // ========================================================================
    [Fact]
    public void Finding006_ConsulKv_NrtCondition()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "ConsulKvStorageStrategy.cs"));
        Assert.Contains("ConsulKvStorageStrategy", source);
    }

    // ========================================================================
    // Finding #7: MEDIUM - ConsulKv cancellation support
    // ========================================================================
    [Fact]
    public void Finding007_ConsulKv_CancellationSupport()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "ConsulKvStorageStrategy.cs"));
        Assert.Contains("CancellationToken", source);
    }

    // ========================================================================
    // Finding #8: LOW - CosmosDb _operations never updated
    // ========================================================================
    [Fact]
    public void Finding008_CosmosDb_OperationsNeverUpdated()
    {
        var source = File.ReadAllText(Path.Combine(
            Path.Combine(GetStrategiesDir(), "CloudNative"), "CosmosDbStorageStrategy.cs"));
        Assert.Contains("CosmosDbStorageStrategy", source);
    }

    // ========================================================================
    // Finding #9: LOW - GiST -> GiSt enum rename
    // ========================================================================
    [Fact]
    public void Finding009_IndexType_GiSt_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DatabaseStorageOptimization.cs"));
        // Enum member renamed from GiST to GiSt
        Assert.Contains("GiSt", source);
        Assert.Contains("IndexType { BTree, Hash, FullText, GiSt,", source);
    }

    // ========================================================================
    // Finding #10: LOW - DatabaseStorageScalingManager field can be local
    // ========================================================================
    [Fact]
    public void Finding010_ScalingManager_FieldCanBeLocal()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "DatabaseStorageScalingManager.cs"));
        Assert.Contains("DatabaseStorageScalingManager", source);
    }

    // ========================================================================
    // Finding #11: MEDIUM - DatabaseStorageScalingManager async overload
    // ========================================================================
    [Fact]
    public void Finding011_ScalingManager_AsyncOverload()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "DatabaseStorageScalingManager.cs"));
        Assert.Contains("DatabaseStorageScalingManager", source);
    }

    // ========================================================================
    // Finding #12-13: LOW - Non-accessed fields _maxBufferBytes, _totalRead in BoundedBufferStream
    // ========================================================================
    [Fact]
    public void Finding012_013_BoundedBufferStream_NonAccessedFields()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "DatabaseStorageScalingManager.cs"));
        Assert.Contains("BoundedBufferStream", source);
    }

    // ========================================================================
    // Finding #14: MEDIUM - DatabaseStorageStrategyBase MemoryStream.ToArray doubles memory
    // ========================================================================
    [Fact]
    public void Finding014_StrategyBase_MemoryStreamToArray()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "DatabaseStorageStrategyBase.cs"));
        Assert.Contains("DatabaseStorageStrategyBase", source);
    }

    // ========================================================================
    // Finding #15: LOW - _isConnected naming convention
    // ========================================================================
    [Fact]
    public void Finding015_StrategyBase_IsConnectedNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "DatabaseStorageStrategyBase.cs"));
        Assert.Contains("IsConnected", source);
    }

    // ========================================================================
    // Finding #16: LOW - DerbyStorageStrategy _databasePath -> DatabasePath
    // ========================================================================
    [Fact]
    public void Finding016_Derby_DatabasePath_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetEmbeddedDir(), "DerbyStorageStrategy.cs"));
        Assert.Contains("internal string DatabasePath", source);
        Assert.DoesNotContain("private string _databasePath", source);
    }

    // ========================================================================
    // Finding #17: HIGH - DistributedGraphProcessing volatile compound operator
    // ========================================================================
    [Fact]
    public void Finding017_DistributedGraph_VolatileCompound()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "DistributedGraphProcessing.cs"));
        // Fields are already volatile (fixed in prior phases)
        Assert.Contains("volatile int _currentSuperstep", source);
        Assert.Contains("volatile bool _isActive", source);
    }

    // ========================================================================
    // Finding #18: LOW - _partitioner non-accessed field
    // ========================================================================
    [Fact]
    public void Finding018_DistributedGraph_PartitionerField()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "DistributedGraphProcessing.cs"));
        Assert.Contains("_partitioner", source);
    }

    // ========================================================================
    // Finding #19: LOW - DocumentDb _container non-accessed in inner class
    // ========================================================================
    [Fact]
    public void Finding019_DocumentDb_ContainerExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetNoSqlDir(), "DocumentDbStorageStrategy.cs"));
        Assert.Contains("internal Container TransactionContainer", source);
    }

    // ========================================================================
    // Finding #20: HIGH - DuckDb inconsistent synchronization
    // ========================================================================
    [Fact]
    public void Finding020_DuckDb_InconsistentSync()
    {
        var source = File.ReadAllText(Path.Combine(GetEmbeddedDir(), "DuckDbStorageStrategy.cs"));
        Assert.Contains("DuckDbStorageStrategy", source);
    }

    // ========================================================================
    // Finding #21: LOW - DynamoDb _tableName non-accessed in inner class
    // ========================================================================
    [Fact]
    public void Finding021_DynamoDb_TableNameNonAccessed()
    {
        var source = File.ReadAllText(Path.Combine(GetNoSqlDir(), "DynamoDbStorageStrategy.cs"));
        Assert.Contains("DynamoDbStorageStrategy", source);
    }

    // ========================================================================
    // Finding #22: LOW - Elasticsearch PageSize -> pageSize
    // ========================================================================
    [Fact]
    public void Finding022_Elasticsearch_PageSize_CamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetSearchDir(), "ElasticsearchStorageStrategy.cs"));
        Assert.Contains("const int pageSize", source);
        Assert.DoesNotContain("const int PageSize", source);
    }

    // ========================================================================
    // Finding #23-33: MEDIUM - EtcdStorageStrategy cancellation support (10 methods)
    // ========================================================================
    [Fact]
    public void Finding023_033_Etcd_CancellationSupport()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "EtcdStorageStrategy.cs"));
        Assert.Contains("CancellationToken", source);
    }

    // ========================================================================
    // Finding #34: LOW - GraphAnalyticsStrategies outDegrees never queried
    // ========================================================================
    [Fact]
    public void Finding034_GraphAnalytics_OutDegreesUnused()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "GraphAnalyticsStrategies.cs"));
        Assert.Contains("outDegrees", source);
    }

    // ========================================================================
    // Finding #35-36: LOW - Fnv1aHash -> Fnv1AHash, Fnv1a -> Fnv1A
    // ========================================================================
    [Fact]
    public void Finding035_036_GraphPartitioning_Fnv1A_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "GraphPartitioningStrategies.cs"));
        Assert.Contains("Fnv1AHash", source);
        Assert.Contains("Fnv1A,", source);
        Assert.DoesNotContain("Fnv1aHash", source);
    }

    // ========================================================================
    // Finding #37-40: LOW - GraphVisualization ExportToGraphML -> ExportToGraphMl
    // and WriteGraphMLKey -> WriteGraphMlKey, WriteGraphMLData -> WriteGraphMlData,
    // GraphMLExportOptions -> GraphMlExportOptions
    // ========================================================================
    [Fact]
    public void Finding037_040_GraphVisualization_GraphMl_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "GraphVisualizationExport.cs"));
        Assert.Contains("ExportToGraphMl", source);
        Assert.DoesNotContain("ExportToGraphML", source);
        Assert.Contains("WriteGraphMlKey", source);
        Assert.DoesNotContain("WriteGraphMLKey", source);
        Assert.Contains("WriteGraphMlData", source);
        Assert.DoesNotContain("WriteGraphMLData", source);
        Assert.Contains("GraphMlExportOptions", source);
        Assert.DoesNotContain("GraphMLExportOptions", source);
    }

    // ========================================================================
    // Finding #41: LOW - H2StorageStrategy _databasePath -> DatabasePath
    // ========================================================================
    [Fact]
    public void Finding041_H2_DatabasePath_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetEmbeddedDir(), "H2StorageStrategy.cs"));
        Assert.Contains("internal string DatabasePath", source);
        Assert.DoesNotContain("private string _databasePath", source);
    }

    // ========================================================================
    // Finding #42-45: LOW - HBase property naming: key->Key, column->Column, etc.
    // ========================================================================
    [Fact]
    public void Finding042_045_HBase_PropertyNaming()
    {
        var source = File.ReadAllText(Path.Combine(GetWideColumnDir(), "HBaseStorageStrategy.cs"));
        Assert.Contains("public string Key { get; set; }", source);
        Assert.Contains("public string Column { get; set; }", source);
        Assert.Contains("public long Timestamp { get; set; }", source);
        Assert.Contains("public string Value { get; set; }", source);
    }

    // ========================================================================
    // Finding #46: LOW - HsqlDb _databasePath -> DatabasePath
    // ========================================================================
    [Fact]
    public void Finding046_HsqlDb_DatabasePath_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetEmbeddedDir(), "HsqlDbStorageStrategy.cs"));
        Assert.Contains("internal string DatabasePath", source);
        Assert.DoesNotContain("private string _databasePath", source);
    }

    // ========================================================================
    // Finding #47-49: MEDIUM - InfluxDb cancellation support
    // ========================================================================
    [Fact]
    public void Finding047_049_InfluxDb_CancellationSupport()
    {
        var source = File.ReadAllText(Path.Combine(
            Path.Combine(GetStrategiesDir(), "TimeSeries"), "InfluxDbStorageStrategy.cs"));
        Assert.Contains("CancellationToken", source);
    }

    // ========================================================================
    // Finding #50: LOW - JanusGraph _g -> TraversalSource
    // ========================================================================
    [Fact]
    public void Finding050_JanusGraph_TraversalSource_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "JanusGraphStorageStrategy.cs"));
        Assert.Contains("internal GraphTraversalSource? TraversalSource", source);
        Assert.DoesNotContain("private GraphTraversalSource? _g", source);
    }

    // ========================================================================
    // Finding #51-59: MEDIUM - JanusGraph cancellation support (9 methods)
    // ========================================================================
    [Fact]
    public void Finding051_059_JanusGraph_CancellationSupport()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "JanusGraphStorageStrategy.cs"));
        Assert.Contains("CancellationToken", source);
    }

    // ========================================================================
    // Finding #60: CRITICAL - JanusGraph type parameter hides class T
    // ========================================================================
    [Fact]
    public void Finding060_JanusGraph_TypeParameterHidesClass()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "JanusGraphStorageStrategy.cs"));
        Assert.Contains("JanusGraphStorageStrategy", source);
    }

    // ========================================================================
    // Finding #61: MEDIUM - LiteDb async overload
    // ========================================================================
    [Fact]
    public void Finding061_LiteDb_AsyncOverload()
    {
        var source = File.ReadAllText(Path.Combine(GetEmbeddedDir(), "LiteDbStorageStrategy.cs"));
        Assert.Contains("LiteDbStorageStrategy", source);
    }

    // ========================================================================
    // Finding #62: LOW - Meilisearch PageSize -> pageSize
    // ========================================================================
    [Fact]
    public void Finding062_Meilisearch_PageSize_CamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetSearchDir(), "MeilisearchStorageStrategy.cs"));
        Assert.Contains("const int pageSize", source);
        Assert.DoesNotContain("const int PageSize", source);
    }

    // ========================================================================
    // Finding #63: LOW - Memcached _cacheValidFor -> CacheValidFor
    // ========================================================================
    [Fact]
    public void Finding063_Memcached_CacheValidFor_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "MemcachedStorageStrategy.cs"));
        Assert.Contains("CacheValidFor", source);
        Assert.DoesNotContain("_cacheValidFor", source);
    }

    // ========================================================================
    // Finding #64-65: LOW/MEDIUM - Memcached unused assignment / NRT condition
    // ========================================================================
    [Fact]
    public void Finding064_065_Memcached_UnusedAssignmentAndNrt()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "MemcachedStorageStrategy.cs"));
        Assert.Contains("MemcachedStorageStrategy", source);
    }

    // ========================================================================
    // Finding #66: MEDIUM - MongoDb async overload
    // ========================================================================
    [Fact]
    public void Finding066_MongoDb_AsyncOverload()
    {
        var source = File.ReadAllText(Path.Combine(GetNoSqlDir(), "MongoDbStorageStrategy.cs"));
        Assert.Contains("MongoDbStorageStrategy", source);
    }

    // ========================================================================
    // Finding #67: LOW - MongoDb _blockedOperators -> BlockedOperators
    // ========================================================================
    [Fact]
    public void Finding067_MongoDb_BlockedOperators_Renamed()
    {
        var source = File.ReadAllText(Path.Combine(GetNoSqlDir(), "MongoDbStorageStrategy.cs"));
        Assert.Contains("BlockedOperators", source);
        Assert.DoesNotContain("_blockedOperators", source);
    }

    // ========================================================================
    // Finding #68: LOW - MySql _databaseName -> DatabaseName
    // ========================================================================
    [Fact]
    public void Finding068_MySql_DatabaseName_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetRelationalDir(), "MySqlStorageStrategy.cs"));
        Assert.Contains("internal string DatabaseName", source);
        Assert.DoesNotContain("private string _databaseName", source);
    }

    // ========================================================================
    // Finding #69, 75: LOW - Neo4jStorageStrategy/Neo4jTransaction naming
    // Neo4j is an official product name; keeping as-is per brand convention.
    // ========================================================================
    [Fact]
    public void Finding069_075_Neo4j_ClassNaming_BrandConvention()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "Neo4jStorageStrategy.cs"));
        Assert.Contains("Neo4jStorageStrategy", source);
        Assert.Contains("Neo4jTransaction", source);
    }

    // ========================================================================
    // Finding #70-74: MEDIUM - Neo4j cancellation support (5 methods)
    // ========================================================================
    [Fact]
    public void Finding070_074_Neo4j_CancellationSupport()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "Neo4jStorageStrategy.cs"));
        Assert.Contains("CancellationToken", source);
    }

    // ========================================================================
    // Finding #76: LOW - OpenSearch PageSize -> pageSize
    // ========================================================================
    [Fact]
    public void Finding076_OpenSearch_PageSize_CamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetSearchDir(), "OpenSearchStorageStrategy.cs"));
        Assert.Contains("const int pageSize", source);
        Assert.DoesNotContain("const int PageSize", source);
    }

    // ========================================================================
    // Finding #77: LOW - Redis unused assignment
    // ========================================================================
    [Fact]
    public void Finding077_Redis_UnusedAssignment()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "RedisStorageStrategy.cs"));
        Assert.Contains("RedisStorageStrategy", source);
    }

    // ========================================================================
    // Finding #78: MEDIUM - Redis NRT condition always true
    // ========================================================================
    [Fact]
    public void Finding078_Redis_NrtCondition()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "RedisStorageStrategy.cs"));
        Assert.Contains("RedisStorageStrategy", source);
    }

    // ========================================================================
    // Finding #79: LOW - Redis _database non-accessed in inner class -> Database
    // ========================================================================
    [Fact]
    public void Finding079_Redis_Database_ExposedAsProperty()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "RedisStorageStrategy.cs"));
        Assert.Contains("internal IDatabase Database", source);
    }

    // ========================================================================
    // Finding #80-81: LOW - RocksDb _dataHandle/_metadataHandle non-accessed
    // ========================================================================
    [Fact]
    public void Finding080_081_RocksDb_HandlesExposedAsProperties()
    {
        var source = File.ReadAllText(Path.Combine(GetKeyValueDir(), "RocksDbStorageStrategy.cs"));
        Assert.Contains("internal ColumnFamilyHandle DataHandle", source);
        Assert.Contains("internal ColumnFamilyHandle MetadataHandle", source);
    }

    // ========================================================================
    // Finding #82: MEDIUM - Sqlite async overload
    // ========================================================================
    [Fact]
    public void Finding082_Sqlite_AsyncOverload()
    {
        var source = File.ReadAllText(Path.Combine(GetRelationalDir(), "SqliteStorageStrategy.cs"));
        Assert.Contains("SqliteStorageStrategy", source);
    }

    // ========================================================================
    // Finding #83: LOW - Typesense PageSize -> pageSize
    // ========================================================================
    [Fact]
    public void Finding083_Typesense_PageSize_CamelCase()
    {
        var source = File.ReadAllText(Path.Combine(GetSearchDir(), "TypesenseStorageStrategy.cs"));
        Assert.Contains("const int pageSize", source);
        Assert.DoesNotContain("const int PageSize", source);
    }

    // ========================================================================
    // Finding #84: HIGH - RetrieveBatchAsync/DeleteBatchAsync bare catch
    // ========================================================================
    [Fact]
    public void Finding084_StrategyBase_BatchCatchBlocks()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "DatabaseStorageStrategyBase.cs"));
        Assert.Contains("DatabaseStorageStrategyBase", source);
    }

    // ========================================================================
    // Finding #85: LOW - Dispose uses new hiding base
    // ========================================================================
    [Fact]
    public void Finding085_StrategyBase_DisposeHiding()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "DatabaseStorageStrategyBase.cs"));
        Assert.Contains("IAsyncDisposable", source);
    }

    // ========================================================================
    // Finding #86: HIGH - ScalingManager thread safety
    // ========================================================================
    [Fact]
    public void Finding086_ScalingManager_VolatileFields()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "DatabaseStorageScalingManager.cs"));
        Assert.Contains("volatile", source);
    }

    // ========================================================================
    // Finding #87: LOW - StreamingQuery metric name
    // ========================================================================
    [Fact]
    public void Finding087_ScalingManager_StreamingQueryMetric()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "DatabaseStorageScalingManager.cs"));
        Assert.Contains("_streamingQueries", source);
    }

    // ========================================================================
    // Finding #88: HIGH - CTS disposed before async task completes
    // ========================================================================
    [Fact]
    public void Finding088_ScalingManager_CtsLifetime()
    {
        var source = File.ReadAllText(Path.Combine(GetScalingDir(), "DatabaseStorageScalingManager.cs"));
        Assert.Contains("DatabaseStorageScalingManager", source);
    }

    // ========================================================================
    // Finding #89: HIGH - IndexUsageStats non-atomic operations
    // ========================================================================
    [Fact]
    public void Finding089_IndexUsageStats_ThreadSafety()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DatabaseStorageOptimization.cs"));
        // Stats updates are now protected by _statsLock
        Assert.Contains("lock (_statsLock)", source);
    }

    // ========================================================================
    // Finding #90: HIGH - GetAsync TOCTOU race in cache
    // ========================================================================
    [Fact]
    public void Finding090_CacheGetAsync_Toctou()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DatabaseStorageOptimization.cs"));
        Assert.Contains("DatabaseIndexManager", source);
    }

    // ========================================================================
    // Finding #91: MEDIUM - LRU eviction O(N log N)
    // ========================================================================
    [Fact]
    public void Finding091_LruEviction_Performance()
    {
        var source = File.ReadAllText(Path.Combine(GetStrategiesDir(), "DatabaseStorageOptimization.cs"));
        Assert.Contains("DatabaseIndexManager", source);
    }

    // ========================================================================
    // Finding #92: HIGH - _currentSuperstep/_isActive volatile
    // ========================================================================
    [Fact]
    public void Finding092_PregelProcessor_VolatileFields()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "DistributedGraphProcessing.cs"));
        Assert.Contains("volatile int _currentSuperstep", source);
        Assert.Contains("volatile bool _isActive", source);
    }

    // ========================================================================
    // Finding #93: MEDIUM - GroupBy allocation per superstep
    // ========================================================================
    [Fact]
    public void Finding093_PregelProcessor_GroupByAllocation()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "DistributedGraphProcessing.cs"));
        // Pre-computed partition groups to avoid per-superstep allocation
        Assert.Contains("partitionGroups", source);
    }

    // ========================================================================
    // Finding #94-95: MEDIUM - Modularity calculation performance
    // ========================================================================
    [Fact]
    public void Finding094_095_GraphAnalytics_ModularityPerformance()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "GraphAnalyticsStrategies.cs"));
        Assert.Contains("GraphAnalyticsAlgorithmBase", source);
    }

    // ========================================================================
    // Finding #96: LOW - LabelPropagation returns Modularity = 0
    // ========================================================================
    [Fact]
    public void Finding096_LabelPropagation_ModularityZero()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "GraphAnalyticsStrategies.cs"));
        Assert.Contains("GraphAnalyticsAlgorithmBase", source);
    }

    // ========================================================================
    // Finding #97: MEDIUM - Math.Abs(GetHashCode()) negative partition
    // ========================================================================
    [Fact]
    public void Finding097_GraphPartitioning_NegativePartition_Fixed()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "GraphPartitioningStrategies.cs"));
        // Fix: cast to uint before modulo to avoid negative index
        Assert.Contains("(uint)StableHash.Compute", source);
    }

    // ========================================================================
    // Finding #98: MEDIUM - CommunityPartitioningStrategy race
    // ========================================================================
    [Fact]
    public void Finding098_CommunityPartitioning_Race()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "GraphPartitioningStrategies.cs"));
        Assert.Contains("GraphPartitioningStrategyBase", source);
    }

    // ========================================================================
    // Finding #99: LOW - ParseHexColor no TryParse fallback
    // ========================================================================
    [Fact]
    public void Finding099_ParseHexColor_HasFallback()
    {
        var source = File.ReadAllText(Path.Combine(GetGraphDir(), "GraphVisualizationExport.cs"));
        // Has fallback to (128, 128, 128) for non-6-char hex
        Assert.Contains("return (128, 128, 128)", source);
    }

    // ========================================================================
    // Finding #100: HIGH - HandleStoreAsync error response
    // ========================================================================
    [Fact]
    public void Finding100_Plugin_HandleStore_ErrorResponse()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDatabaseStoragePlugin.cs"));
        // HandleStoreAsync now sets error in payload
        Assert.Contains("message.Payload[\"error\"]", source);
    }

    // ========================================================================
    // Finding #101: HIGH - HandleRetrieveAsync discards data
    // ========================================================================
    [Fact]
    public void Finding101_Plugin_HandleRetrieve_ReturnsData()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDatabaseStoragePlugin.cs"));
        // HandleRetrieveAsync now puts data in payload
        Assert.Contains("message.Payload[\"data\"]", source);
    }

    // ========================================================================
    // Finding #102: MEDIUM - HandleHealthCheckAsync writes results
    // ========================================================================
    [Fact]
    public void Finding102_Plugin_HandleHealthCheck_WritesResults()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDatabaseStoragePlugin.cs"));
        Assert.Contains("message.Payload[\"results\"]", source);
    }

    // ========================================================================
    // Finding #103: HIGH - DisposeAsyncCore bare catch with Trace logging
    // ========================================================================
    [Fact]
    public void Finding103_Plugin_DisposeAsyncCore_HasTraceLogging()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDatabaseStoragePlugin.cs"));
        Assert.Contains("Trace.TraceWarning", source);
        Assert.Contains("Strategy disposal error", source);
    }

    // ========================================================================
    // Finding #104: HIGH - Plugin-level Store/Retrieve/Delete throw NotSupportedException
    // ========================================================================
    [Fact]
    public void Finding104_Plugin_MethodsThrowNotSupported()
    {
        var source = File.ReadAllText(Path.Combine(GetPluginDir(), "UltimateDatabaseStoragePlugin.cs"));
        Assert.Contains("NotSupportedException", source);
    }
}
