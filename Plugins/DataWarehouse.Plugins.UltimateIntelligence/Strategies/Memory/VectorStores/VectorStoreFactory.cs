using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Configuration source for vector store creation.
/// </summary>
public sealed class VectorStoreConfiguration
{
    /// <summary>Store type identifier (e.g., "pinecone", "qdrant").</summary>
    public required string StoreType { get; init; }

    /// <summary>Instance name for this configuration.</summary>
    public string? InstanceName { get; init; }

    /// <summary>Store-specific configuration options.</summary>
    public Dictionary<string, object> Options { get; init; } = new();

    /// <summary>Whether this store is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Priority for this store (lower = higher priority).</summary>
    public int Priority { get; init; } = 100;

    /// <summary>Tags for categorization.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Factory for creating production vector store instances from configuration.
/// </summary>
/// <remarks>
/// The factory supports:
/// <list type="bullet">
/// <item>Configuration-based store creation</item>
/// <item>Automatic option mapping from dictionaries</item>
/// <item>Instance caching and lifecycle management</item>
/// <item>Health validation on creation</item>
/// </list>
/// </remarks>
public sealed class VectorStoreFactory : IAsyncDisposable
{
    private readonly VectorStoreRegistry _registry;
    private readonly HttpClient _httpClient;
    private static readonly HttpClient SharedHttpClient = new HttpClient();
    private readonly Dictionary<string, IProductionVectorStore> _instances = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new vector store factory.
    /// </summary>
    /// <param name="registry">Optional registry (uses singleton if not provided).</param>
    /// <param name="httpClient">Optional shared HTTP client.</param>
    public VectorStoreFactory(VectorStoreRegistry? registry = null, HttpClient? httpClient = null)
    {
        _registry = registry ?? VectorStoreRegistry.Instance;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    /// <summary>
    /// Creates a vector store from configuration.
    /// </summary>
    /// <param name="config">Store configuration.</param>
    /// <param name="validateHealth">Whether to validate health on creation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created vector store instance.</returns>
    public async Task<IProductionVectorStore> CreateAsync(
        VectorStoreConfiguration config,
        bool validateHealth = true,
        CancellationToken ct = default)
    {
        var registration = _registry.GetRegistration(config.StoreType)
            ?? throw new ArgumentException($"Unknown store type: {config.StoreType}");

        var instanceKey = config.InstanceName ?? config.StoreType;

        lock (_lock)
        {
            if (_instances.TryGetValue(instanceKey, out var existing))
            {
                return existing;
            }
        }

        // Create options instance
        var options = CreateOptions(registration.OptionsType, config.Options);

        // Create store instance
        var store = CreateStore(registration.StoreType, options);

        if (validateHealth)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            if (!await store.IsHealthyAsync(cts.Token))
            {
                await store.DisposeAsync();
                throw new InvalidOperationException($"Store {config.StoreType} failed health check");
            }
        }

        lock (_lock)
        {
            _instances[instanceKey] = store;
        }

        _registry.RegisterInstance(store);

        return store;
    }

    /// <summary>
    /// Creates a Pinecone vector store.
    /// </summary>
    public Task<PineconeVectorStore> CreatePineconeAsync(
        string apiKey,
        string environment,
        string indexName,
        string? ns = null,
        bool isServerless = false,
        string? projectId = null,
        CancellationToken ct = default)
    {
        var options = new PineconeOptions
        {
            ApiKey = apiKey,
            Environment = environment,
            IndexName = indexName,
            Namespace = ns,
            IsServerless = isServerless,
            ProjectId = projectId
        };

        return Task.FromResult(new PineconeVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates a Qdrant vector store.
    /// </summary>
    public Task<QdrantVectorStore> CreateQdrantAsync(
        string host = "http://localhost:6333",
        string collection = "datawarehouse",
        string? apiKey = null,
        CancellationToken ct = default)
    {
        var options = new QdrantOptions
        {
            Host = host,
            Collection = collection,
            ApiKey = apiKey
        };

        return Task.FromResult(new QdrantVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates a Weaviate vector store.
    /// </summary>
    public Task<WeaviateVectorStore> CreateWeaviateAsync(
        string host = "http://localhost:8080",
        string className = "DataWarehouse",
        string? apiKey = null,
        bool enableHybridSearch = false,
        CancellationToken ct = default)
    {
        var options = new WeaviateOptions
        {
            Host = host,
            ClassName = className,
            ApiKey = apiKey,
            EnableHybridSearch = enableHybridSearch
        };

        return Task.FromResult(new WeaviateVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates a Milvus vector store.
    /// </summary>
    public Task<MilvusVectorStore> CreateMilvusAsync(
        string host = "http://localhost:19530",
        string collection = "datawarehouse",
        string? apiKey = null,
        MilvusIndexType indexType = MilvusIndexType.Hnsw,
        CancellationToken ct = default)
    {
        var options = new MilvusOptions
        {
            Host = host,
            Collection = collection,
            ApiKey = apiKey,
            IndexType = indexType
        };

        return Task.FromResult(new MilvusVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates a Chroma vector store.
    /// </summary>
    public Task<ChromaVectorStore> CreateChromaAsync(
        string host = "http://localhost:8000",
        string collection = "datawarehouse",
        bool autoCreate = true,
        CancellationToken ct = default)
    {
        var options = new ChromaOptions
        {
            Host = host,
            Collection = collection,
            AutoCreateCollection = autoCreate
        };

        return Task.FromResult(new ChromaVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates a Redis vector store.
    /// </summary>
    public Task<RedisVectorStore> CreateRedisAsync(
        string connectionString = "localhost:6379",
        string indexName = "datawarehouse_idx",
        string? restApiEndpoint = null,
        RedisIndexAlgorithm algorithm = RedisIndexAlgorithm.Hnsw,
        CancellationToken ct = default)
    {
        var options = new RedisOptions
        {
            ConnectionString = connectionString,
            IndexName = indexName,
            RestApiEndpoint = restApiEndpoint,
            Algorithm = algorithm
        };

        return Task.FromResult(new RedisVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates a PostgreSQL pgvector store.
    /// </summary>
    public Task<PgVectorStore> CreatePgVectorAsync(
        string connectionString,
        string tableName = "vector_embeddings",
        PgVectorIndexType indexType = PgVectorIndexType.Hnsw,
        CancellationToken ct = default)
    {
        var options = new PgVectorOptions
        {
            ConnectionString = connectionString,
            TableName = tableName,
            IndexType = indexType
        };

        return Task.FromResult(new PgVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates an Elasticsearch vector store.
    /// </summary>
    public Task<ElasticsearchVectorStore> CreateElasticsearchAsync(
        string host = "http://localhost:9200",
        string indexName = "datawarehouse-vectors",
        string? username = null,
        string? password = null,
        string? apiKey = null,
        ElasticsearchKnnType knnType = ElasticsearchKnnType.Approximate,
        CancellationToken ct = default)
    {
        var options = new ElasticsearchOptions
        {
            Host = host,
            IndexName = indexName,
            Username = username,
            Password = password,
            ApiKey = apiKey,
            KnnType = knnType
        };

        return Task.FromResult(new ElasticsearchVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates an Azure AI Search vector store.
    /// </summary>
    public Task<AzureAISearchVectorStore> CreateAzureAISearchAsync(
        string endpoint,
        string apiKey,
        string indexName = "datawarehouse-vectors",
        bool useSemanticRanking = false,
        CancellationToken ct = default)
    {
        var options = new AzureAISearchOptions
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            IndexName = indexName,
            UseSemanticRanking = useSemanticRanking
        };

        return Task.FromResult(new AzureAISearchVectorStore(options, _httpClient));
    }

    /// <summary>
    /// Creates a hybrid vector store combining multiple backends.
    /// </summary>
    public Task<HybridVectorStore> CreateHybridAsync(
        IProductionVectorStore primaryStore,
        IProductionVectorStore? secondaryStore = null,
        HybridVectorStoreOptions? options = null,
        CancellationToken ct = default)
    {
        return Task.FromResult(new HybridVectorStore(
            primaryStore,
            secondaryStore,
            options ?? new HybridVectorStoreOptions()));
    }

    /// <summary>
    /// Creates multiple stores from a list of configurations.
    /// </summary>
    public async Task<IEnumerable<IProductionVectorStore>> CreateFromConfigurationsAsync(
        IEnumerable<VectorStoreConfiguration> configs,
        bool validateHealth = true,
        CancellationToken ct = default)
    {
        var stores = new List<IProductionVectorStore>();

        foreach (var config in configs)
        {
            if (!config.Enabled) continue;

            try
            {
                var store = await CreateAsync(config, validateHealth, ct);
                stores.Add(store);
            }
            catch (Exception ex)
            {
                // Log and continue with other stores
                System.Diagnostics.Debug.WriteLine($"Failed to create store {config.StoreType}: {ex.Message}");
            }
        }

        return stores;
    }

    private object CreateOptions(Type optionsType, Dictionary<string, object> config)
    {
        // Serialize and deserialize to map configuration to options type
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return JsonSerializer.Deserialize(json, optionsType)
            ?? throw new InvalidOperationException($"Failed to create options of type {optionsType.Name}");
    }

    private IProductionVectorStore CreateStore(Type storeType, object options)
    {
        // Look for constructor with (options, HttpClient) signature
        var ctor = storeType.GetConstructor(new[] { options.GetType(), typeof(HttpClient) });

        if (ctor != null)
        {
            return (IProductionVectorStore)ctor.Invoke(new[] { options, _httpClient });
        }

        // Try constructor with just options
        ctor = storeType.GetConstructor(new[] { options.GetType() });

        if (ctor != null)
        {
            return (IProductionVectorStore)ctor.Invoke(new[] { options });
        }

        throw new InvalidOperationException(
            $"No suitable constructor found for {storeType.Name}");
    }

    /// <summary>
    /// Gets a previously created store instance.
    /// </summary>
    public IProductionVectorStore? GetInstance(string instanceName)
    {
        lock (_lock)
        {
            return _instances.TryGetValue(instanceName, out var store) ? store : null;
        }
    }

    /// <summary>
    /// Disposes all created store instances.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<IProductionVectorStore> toDispose;

        lock (_lock)
        {
            toDispose = new List<IProductionVectorStore>(_instances.Values);
            _instances.Clear();
        }

        foreach (var store in toDispose)
        {
            try
            {
                _registry.UnregisterInstance(store.StoreId);
                await store.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors
            }
        }
    }
}
