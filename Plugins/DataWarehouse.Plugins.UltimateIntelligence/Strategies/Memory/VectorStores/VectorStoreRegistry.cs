using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.VectorStores;

/// <summary>
/// Vector store registration information.
/// </summary>
public sealed class VectorStoreRegistration
{
    /// <summary>Unique identifier for the store.</summary>
    public required string StoreId { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Store type.</summary>
    public required Type StoreType { get; init; }

    /// <summary>Options type required for construction.</summary>
    public required Type OptionsType { get; init; }

    /// <summary>Description of the store.</summary>
    public string? Description { get; init; }

    /// <summary>Provider name (e.g., "Pinecone", "Qdrant").</summary>
    public string? ProviderName { get; init; }

    /// <summary>Supported distance metrics.</summary>
    public DistanceMetric[] SupportedMetrics { get; init; } = new[] { DistanceMetric.Cosine };

    /// <summary>Whether the store supports cloud/managed deployment.</summary>
    public bool SupportsCloud { get; init; }

    /// <summary>Whether the store supports self-hosted deployment.</summary>
    public bool SupportsSelfHosted { get; init; }

    /// <summary>Priority for auto-selection (lower = higher priority).</summary>
    public int Priority { get; init; } = 100;

    /// <summary>Tags for categorization and discovery.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Registry for production vector stores with auto-discovery capabilities.
/// </summary>
/// <remarks>
/// The registry provides:
/// <list type="bullet">
/// <item>Registration of vector store implementations</item>
/// <item>Auto-discovery of stores via reflection</item>
/// <item>Store lookup by ID, provider, or capabilities</item>
/// <item>Health monitoring across all registered stores</item>
/// </list>
/// </remarks>
public sealed class VectorStoreRegistry
{
    private readonly ConcurrentDictionary<string, VectorStoreRegistration> _registrations = new();
    private readonly ConcurrentDictionary<string, IProductionVectorStore> _instances = new();
    private static readonly Lazy<VectorStoreRegistry> _instance = new(() => new VectorStoreRegistry());

    /// <summary>
    /// Gets the singleton instance of the registry.
    /// </summary>
    public static VectorStoreRegistry Instance => _instance.Value;

    /// <summary>
    /// Gets all registered stores.
    /// </summary>
    public IEnumerable<VectorStoreRegistration> Registrations => _registrations.Values;

    /// <summary>
    /// Gets all instantiated stores.
    /// </summary>
    public IEnumerable<IProductionVectorStore> Instances => _instances.Values;

    /// <summary>
    /// Private constructor for singleton pattern.
    /// </summary>
    private VectorStoreRegistry()
    {
        RegisterBuiltInStores();
    }

    private void RegisterBuiltInStores()
    {
        Register(new VectorStoreRegistration
        {
            StoreId = "pinecone",
            DisplayName = "Pinecone",
            StoreType = typeof(PineconeVectorStore),
            OptionsType = typeof(PineconeOptions),
            Description = "Managed vector database for production ML workloads",
            ProviderName = "Pinecone",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct },
            SupportsCloud = true,
            SupportsSelfHosted = false,
            Priority = 10,
            Tags = new[] { "managed", "cloud", "production", "hybrid-search" }
        });

        Register(new VectorStoreRegistration
        {
            StoreId = "qdrant",
            DisplayName = "Qdrant",
            StoreType = typeof(QdrantVectorStore),
            OptionsType = typeof(QdrantOptions),
            Description = "High-performance vector search engine",
            ProviderName = "Qdrant",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct, DistanceMetric.Manhattan },
            SupportsCloud = true,
            SupportsSelfHosted = true,
            Priority = 20,
            Tags = new[] { "rust", "open-source", "filtering", "cloud", "self-hosted" }
        });

        Register(new VectorStoreRegistration
        {
            StoreId = "weaviate",
            DisplayName = "Weaviate",
            StoreType = typeof(WeaviateVectorStore),
            OptionsType = typeof(WeaviateOptions),
            Description = "Open-source vector database with GraphQL interface",
            ProviderName = "Weaviate",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct, DistanceMetric.Manhattan },
            SupportsCloud = true,
            SupportsSelfHosted = true,
            Priority = 25,
            Tags = new[] { "graphql", "open-source", "hybrid-search", "multi-tenancy" }
        });

        Register(new VectorStoreRegistration
        {
            StoreId = "milvus",
            DisplayName = "Milvus",
            StoreType = typeof(MilvusVectorStore),
            OptionsType = typeof(MilvusOptions),
            Description = "Cloud-native vector database for billion-scale search",
            ProviderName = "Milvus",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct },
            SupportsCloud = true,
            SupportsSelfHosted = true,
            Priority = 30,
            Tags = new[] { "billion-scale", "distributed", "cloud-native", "gpu" }
        });

        Register(new VectorStoreRegistration
        {
            StoreId = "chroma",
            DisplayName = "Chroma",
            StoreType = typeof(ChromaVectorStore),
            OptionsType = typeof(ChromaOptions),
            Description = "AI-native open-source embedding database",
            ProviderName = "Chroma",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct },
            SupportsCloud = false,
            SupportsSelfHosted = true,
            Priority = 40,
            Tags = new[] { "simple", "python", "ai-native", "development" }
        });

        Register(new VectorStoreRegistration
        {
            StoreId = "redis",
            DisplayName = "Redis Stack",
            StoreType = typeof(RedisVectorStore),
            OptionsType = typeof(RedisOptions),
            Description = "Redis Stack with RediSearch vector similarity",
            ProviderName = "Redis",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct },
            SupportsCloud = true,
            SupportsSelfHosted = true,
            Priority = 35,
            Tags = new[] { "in-memory", "low-latency", "hybrid-queries" }
        });

        Register(new VectorStoreRegistration
        {
            StoreId = "pgvector",
            DisplayName = "PostgreSQL pgvector",
            StoreType = typeof(PgVectorStore),
            OptionsType = typeof(PgVectorOptions),
            Description = "Vector similarity search in PostgreSQL",
            ProviderName = "PostgreSQL",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct },
            SupportsCloud = true,
            SupportsSelfHosted = true,
            Priority = 45,
            Tags = new[] { "sql", "postgres", "acid", "integrated" }
        });

        Register(new VectorStoreRegistration
        {
            StoreId = "elasticsearch",
            DisplayName = "Elasticsearch",
            StoreType = typeof(ElasticsearchVectorStore),
            OptionsType = typeof(ElasticsearchOptions),
            Description = "Elasticsearch/OpenSearch kNN vector search",
            ProviderName = "Elasticsearch",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct },
            SupportsCloud = true,
            SupportsSelfHosted = true,
            Priority = 50,
            Tags = new[] { "distributed", "scalable", "hybrid-search", "full-text" }
        });

        Register(new VectorStoreRegistration
        {
            StoreId = "azure-ai-search",
            DisplayName = "Azure AI Search",
            StoreType = typeof(AzureAISearchVectorStore),
            OptionsType = typeof(AzureAISearchOptions),
            Description = "Azure AI Search with vector and semantic ranking",
            ProviderName = "Microsoft Azure",
            SupportedMetrics = new[] { DistanceMetric.Cosine, DistanceMetric.Euclidean, DistanceMetric.DotProduct },
            SupportsCloud = true,
            SupportsSelfHosted = false,
            Priority = 15,
            Tags = new[] { "azure", "enterprise", "semantic-ranking", "managed" }
        });
    }

    /// <summary>
    /// Registers a vector store.
    /// </summary>
    /// <param name="registration">Store registration information.</param>
    public void Register(VectorStoreRegistration registration)
    {
        _registrations[registration.StoreId] = registration;
    }

    /// <summary>
    /// Unregisters a vector store.
    /// </summary>
    /// <param name="storeId">Store ID to unregister.</param>
    public bool Unregister(string storeId)
    {
        return _registrations.TryRemove(storeId, out _);
    }

    /// <summary>
    /// Gets a registration by store ID.
    /// </summary>
    public VectorStoreRegistration? GetRegistration(string storeId)
    {
        return _registrations.TryGetValue(storeId, out var reg) ? reg : null;
    }

    /// <summary>
    /// Gets registrations by provider name.
    /// </summary>
    public IEnumerable<VectorStoreRegistration> GetByProvider(string providerName)
    {
        return _registrations.Values
            .Where(r => r.ProviderName?.Equals(providerName, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Gets registrations that support a specific metric.
    /// </summary>
    public IEnumerable<VectorStoreRegistration> GetByMetric(DistanceMetric metric)
    {
        return _registrations.Values
            .Where(r => r.SupportedMetrics.Contains(metric))
            .OrderBy(r => r.Priority);
    }

    /// <summary>
    /// Gets registrations by tag.
    /// </summary>
    public IEnumerable<VectorStoreRegistration> GetByTag(string tag)
    {
        return _registrations.Values
            .Where(r => r.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Priority);
    }

    /// <summary>
    /// Gets registrations that support cloud deployment.
    /// </summary>
    public IEnumerable<VectorStoreRegistration> GetCloudStores()
    {
        return _registrations.Values
            .Where(r => r.SupportsCloud)
            .OrderBy(r => r.Priority);
    }

    /// <summary>
    /// Gets registrations that support self-hosted deployment.
    /// </summary>
    public IEnumerable<VectorStoreRegistration> GetSelfHostedStores()
    {
        return _registrations.Values
            .Where(r => r.SupportsSelfHosted)
            .OrderBy(r => r.Priority);
    }

    /// <summary>
    /// Registers a store instance.
    /// </summary>
    public void RegisterInstance(IProductionVectorStore store)
    {
        _instances[store.StoreId] = store;
    }

    /// <summary>
    /// Unregisters a store instance.
    /// </summary>
    public bool UnregisterInstance(string storeId)
    {
        return _instances.TryRemove(storeId, out _);
    }

    /// <summary>
    /// Gets a registered store instance.
    /// </summary>
    public IProductionVectorStore? GetInstance(string storeId)
    {
        return _instances.TryGetValue(storeId, out var store) ? store : null;
    }

    /// <summary>
    /// Gets all healthy store instances.
    /// </summary>
    public async Task<IEnumerable<IProductionVectorStore>> GetHealthyInstancesAsync(CancellationToken ct = default)
    {
        var healthyStores = new List<IProductionVectorStore>();

        foreach (var store in _instances.Values)
        {
            try
            {
                if (await store.IsHealthyAsync(ct))
                {
                    healthyStores.Add(store);
                }
            }
            catch
            {
                // Skip unhealthy stores
            }
        }

        return healthyStores;
    }

    /// <summary>
    /// Discovers and registers vector stores from the specified assembly.
    /// </summary>
    public void DiscoverFromAssembly(Assembly assembly)
    {
        var storeTypes = assembly.GetTypes()
            .Where(t => typeof(IProductionVectorStore).IsAssignableFrom(t)
                     && !t.IsAbstract
                     && !t.IsInterface);

        foreach (var type in storeTypes)
        {
            // Look for a matching options type
            var optionsType = assembly.GetTypes()
                .FirstOrDefault(t => t.Name == type.Name.Replace("VectorStore", "Options")
                                  || t.Name == type.Name + "Options");

            if (optionsType != null)
            {
                var storeId = type.Name
                    .Replace("VectorStore", "")
                    .Replace("Store", "")
                    .ToLowerInvariant();

                if (!_registrations.ContainsKey(storeId))
                {
                    Register(new VectorStoreRegistration
                    {
                        StoreId = storeId,
                        DisplayName = type.Name.Replace("VectorStore", " Vector Store"),
                        StoreType = type,
                        OptionsType = optionsType,
                        Description = "Auto-discovered vector store",
                        Priority = 100,
                        Tags = new[] { "auto-discovered" }
                    });
                }
            }
        }
    }

    /// <summary>
    /// Gets aggregate statistics across all registered instances.
    /// </summary>
    public async Task<Dictionary<string, VectorStoreStatistics>> GetAllStatisticsAsync(CancellationToken ct = default)
    {
        var stats = new Dictionary<string, VectorStoreStatistics>();

        foreach (var kvp in _instances)
        {
            try
            {
                stats[kvp.Key] = await kvp.Value.GetStatisticsAsync(ct);
            }
            catch
            {
                // Skip stores that fail to return statistics
            }
        }

        return stats;
    }
}
