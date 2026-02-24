using System.Reflection;
using DataWarehouse.SDK.Utilities;
using System.Diagnostics;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Embeddings;

/// <summary>
/// Registry for embedding providers with auto-discovery and lifecycle management.
///
/// Features:
/// - Auto-discovery of embedding providers via reflection
/// - Thread-safe provider registration and retrieval
/// - Lazy initialization of providers
/// - Provider health monitoring
/// - Metrics aggregation across providers
/// </summary>
public sealed class EmbeddingProviderRegistry : IDisposable
{
    private readonly BoundedDictionary<string, ProviderRegistration> _registrations = new BoundedDictionary<string, ProviderRegistration>(1000);
    private readonly BoundedDictionary<string, IEmbeddingProvider> _instances = new BoundedDictionary<string, IEmbeddingProvider>(1000);
    private readonly object _initLock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the singleton instance of the registry.
    /// </summary>
    public static EmbeddingProviderRegistry Instance { get; } = new();

    /// <summary>
    /// Gets all registered provider IDs.
    /// </summary>
    public IEnumerable<string> RegisteredProviders => _registrations.Keys;

    /// <summary>
    /// Gets the count of registered providers.
    /// </summary>
    public int Count => _registrations.Count;

    private EmbeddingProviderRegistry()
    {
        // Register built-in providers
        RegisterBuiltInProviders();
    }

    /// <summary>
    /// Registers a provider type with the registry.
    /// </summary>
    /// <typeparam name="T">The provider type.</typeparam>
    /// <param name="providerId">The unique provider identifier.</param>
    /// <param name="factory">Optional factory function for creating instances.</param>
    public void Register<T>(string providerId, Func<EmbeddingProviderConfig, T>? factory = null)
        where T : class, IEmbeddingProvider
    {
        if (string.IsNullOrWhiteSpace(providerId))
            throw new ArgumentException("Provider ID cannot be null or empty", nameof(providerId));

        var registration = new ProviderRegistration
        {
            ProviderId = providerId,
            ProviderType = typeof(T),
            Factory = factory != null
                ? config => factory(config)
                : null
        };

        _registrations[providerId] = registration;
    }

    /// <summary>
    /// Registers a provider instance directly.
    /// </summary>
    /// <param name="provider">The provider instance to register.</param>
    public void RegisterInstance(IEmbeddingProvider provider)
    {
        if (provider == null)
            throw new ArgumentNullException(nameof(provider));

        var registration = new ProviderRegistration
        {
            ProviderId = provider.ProviderId,
            ProviderType = provider.GetType(),
            Instance = provider
        };

        _registrations[provider.ProviderId] = registration;
        _instances[provider.ProviderId] = provider;
    }

    /// <summary>
    /// Gets or creates a provider instance by ID.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="config">Configuration for the provider.</param>
    /// <returns>The provider instance.</returns>
    public IEmbeddingProvider GetProvider(string providerId, EmbeddingProviderConfig config)
    {
        if (!_registrations.TryGetValue(providerId, out var registration))
            throw new KeyNotFoundException($"Provider '{providerId}' is not registered");

        // Return existing instance if available
        if (_instances.TryGetValue(providerId, out var existingInstance))
            return existingInstance;

        // Create new instance
        lock (_initLock)
        {
            if (_instances.TryGetValue(providerId, out existingInstance))
                return existingInstance;

            var instance = CreateProviderInstance(registration, config);
            _instances[providerId] = instance;
            return instance;
        }
    }

    /// <summary>
    /// Gets a provider instance if it exists, without creating one.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>The provider instance if exists, null otherwise.</returns>
    public IEmbeddingProvider? GetProviderIfExists(string providerId)
    {
        return _instances.TryGetValue(providerId, out var instance) ? instance : null;
    }

    /// <summary>
    /// Checks if a provider is registered.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>True if registered, false otherwise.</returns>
    public bool IsRegistered(string providerId)
    {
        return _registrations.ContainsKey(providerId);
    }

    /// <summary>
    /// Checks if a provider instance exists.
    /// </summary>
    /// <param name="providerId">The provider ID.</param>
    /// <returns>True if an instance exists, false otherwise.</returns>
    public bool HasInstance(string providerId)
    {
        return _instances.ContainsKey(providerId);
    }

    /// <summary>
    /// Unregisters a provider.
    /// </summary>
    /// <param name="providerId">The provider ID to unregister.</param>
    /// <returns>True if unregistered, false if not found.</returns>
    public bool Unregister(string providerId)
    {
        if (_instances.TryRemove(providerId, out var instance))
        {
            instance.Dispose();
        }
        return _registrations.TryRemove(providerId, out _);
    }

    /// <summary>
    /// Gets information about all registered providers.
    /// </summary>
    /// <returns>List of provider registration info.</returns>
    public IReadOnlyList<ProviderInfo> GetProviderInfo()
    {
        var result = new List<ProviderInfo>();

        foreach (var registration in _registrations.Values)
        {
            var hasInstance = _instances.TryGetValue(registration.ProviderId, out var instance);
            result.Add(new ProviderInfo
            {
                ProviderId = registration.ProviderId,
                ProviderType = registration.ProviderType,
                HasInstance = hasInstance,
                Dimensions = hasInstance ? instance!.VectorDimensions : 0,
                MaxTokens = hasInstance ? instance!.MaxTokens : 0,
                SupportsMultipleTexts = hasInstance && instance!.SupportsMultipleTexts
            });
        }

        return result;
    }

    /// <summary>
    /// Validates all provider connections.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary of provider IDs to validation results.</returns>
    public async Task<IReadOnlyDictionary<string, bool>> ValidateAllAsync(CancellationToken ct = default)
    {
        var results = new BoundedDictionary<string, bool>(1000);
        var tasks = new List<Task>();

        foreach (var kvp in _instances)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var isValid = await kvp.Value.ValidateConnectionAsync(ct);
                    results[kvp.Key] = isValid;
                }
                catch
                {
                    Debug.WriteLine($"Caught exception in EmbeddingProviderRegistry.cs");
                    results[kvp.Key] = false;
                }
            }, ct));
        }

        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Gets aggregated metrics across all provider instances.
    /// </summary>
    /// <returns>Aggregated metrics.</returns>
    public EmbeddingRegistryMetrics GetAggregatedMetrics()
    {
        var metrics = new EmbeddingRegistryMetrics();

        foreach (var instance in _instances.Values)
        {
            if (instance is IEmbeddingProviderExtended extended)
            {
                var providerMetrics = extended.GetMetrics();
                metrics.TotalRequests += providerMetrics.TotalRequests;
                metrics.SuccessfulRequests += providerMetrics.SuccessfulRequests;
                metrics.FailedRequests += providerMetrics.FailedRequests;
                metrics.TotalTokensProcessed += providerMetrics.TotalTokensProcessed;
                metrics.TotalEmbeddingsGenerated += providerMetrics.TotalEmbeddingsGenerated;
                metrics.TotalLatencyMs += providerMetrics.TotalLatencyMs;
                metrics.EstimatedCostUsd += providerMetrics.EstimatedCostUsd;
                metrics.RateLimitHits += providerMetrics.RateLimitHits;
            }
        }

        metrics.ProviderCount = _instances.Count;
        return metrics;
    }

    /// <summary>
    /// Auto-discovers and registers providers from the current assembly.
    /// </summary>
    public void DiscoverProviders()
    {
        DiscoverProviders(Assembly.GetExecutingAssembly());
    }

    /// <summary>
    /// Auto-discovers and registers providers from the specified assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    public void DiscoverProviders(Assembly assembly)
    {
        var providerTypes = assembly.GetTypes()
            .Where(t => !t.IsAbstract &&
                       !t.IsInterface &&
                       typeof(IEmbeddingProvider).IsAssignableFrom(t) &&
                       t.GetConstructor(new[] { typeof(EmbeddingProviderConfig), typeof(HttpClient) }) != null ||
                       t.GetConstructor(new[] { typeof(EmbeddingProviderConfig) }) != null);

        foreach (var type in providerTypes)
        {
            try
            {
                // Create a temporary instance to get the provider ID
                var tempInstance = CreateTemporaryInstance(type);
                if (tempInstance != null)
                {
                    var registration = new ProviderRegistration
                    {
                        ProviderId = tempInstance.ProviderId,
                        ProviderType = type
                    };
                    _registrations.TryAdd(tempInstance.ProviderId, registration);
                    tempInstance.Dispose();
                }
            }
            catch
            {
                Debug.WriteLine($"Caught exception in EmbeddingProviderRegistry.cs");
                // Skip types that can't be instantiated
            }
        }
    }

    private void RegisterBuiltInProviders()
    {
        // Register all built-in providers with their factories
        _registrations["openai"] = new ProviderRegistration
        {
            ProviderId = "openai",
            ProviderType = typeof(OpenAIEmbeddingProvider),
            Factory = config => new OpenAIEmbeddingProvider(config)
        };

        _registrations["azure-openai"] = new ProviderRegistration
        {
            ProviderId = "azure-openai",
            ProviderType = typeof(AzureOpenAIEmbeddingProvider)
            // Factory requires deployment name, so left null
        };

        _registrations["cohere"] = new ProviderRegistration
        {
            ProviderId = "cohere",
            ProviderType = typeof(CohereEmbeddingProvider),
            Factory = config => new CohereEmbeddingProvider(config)
        };

        _registrations["huggingface"] = new ProviderRegistration
        {
            ProviderId = "huggingface",
            ProviderType = typeof(HuggingFaceEmbeddingProvider),
            Factory = config => new HuggingFaceEmbeddingProvider(config)
        };

        _registrations["ollama"] = new ProviderRegistration
        {
            ProviderId = "ollama",
            ProviderType = typeof(OllamaEmbeddingProvider),
            Factory = config => new OllamaEmbeddingProvider(config)
        };

        _registrations["onnx"] = new ProviderRegistration
        {
            ProviderId = "onnx",
            ProviderType = typeof(ONNXEmbeddingProvider)
            // Factory requires model path, so left null
        };

        _registrations["voyageai"] = new ProviderRegistration
        {
            ProviderId = "voyageai",
            ProviderType = typeof(VoyageAIEmbeddingProvider),
            Factory = config => new VoyageAIEmbeddingProvider(config)
        };

        _registrations["jina"] = new ProviderRegistration
        {
            ProviderId = "jina",
            ProviderType = typeof(JinaEmbeddingProvider),
            Factory = config => new JinaEmbeddingProvider(config)
        };
    }

    private IEmbeddingProvider CreateProviderInstance(ProviderRegistration registration, EmbeddingProviderConfig config)
    {
        // Use factory if available
        if (registration.Factory != null)
        {
            return registration.Factory(config);
        }

        // Return existing instance if available
        if (registration.Instance != null)
        {
            return registration.Instance;
        }

        // Try to create using reflection
        var type = registration.ProviderType;

        // Try constructor with config and HttpClient
        var ctor = type.GetConstructor(new[] { typeof(EmbeddingProviderConfig), typeof(HttpClient) });
        if (ctor != null)
        {
            return (IEmbeddingProvider)ctor.Invoke(new object?[] { config, null });
        }

        // Try constructor with just config
        ctor = type.GetConstructor(new[] { typeof(EmbeddingProviderConfig) });
        if (ctor != null)
        {
            return (IEmbeddingProvider)ctor.Invoke(new object[] { config });
        }

        throw new InvalidOperationException($"Cannot create instance of provider type {type.Name}. " +
            "Provider must have a constructor accepting EmbeddingProviderConfig.");
    }

    private IEmbeddingProvider? CreateTemporaryInstance(Type type)
    {
        try
        {
            var dummyConfig = new EmbeddingProviderConfig { ApiKey = "dummy" };

            var ctor = type.GetConstructor(new[] { typeof(EmbeddingProviderConfig), typeof(HttpClient) });
            if (ctor != null)
            {
                return (IEmbeddingProvider)ctor.Invoke(new object?[] { dummyConfig, null });
            }

            ctor = type.GetConstructor(new[] { typeof(EmbeddingProviderConfig) });
            if (ctor != null)
            {
                return (IEmbeddingProvider)ctor.Invoke(new object[] { dummyConfig });
            }
        }
        catch
        {
            Debug.WriteLine($"Caught exception in EmbeddingProviderRegistry.cs");
            // Ignore
        }

        return null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var instance in _instances.Values)
        {
            try
            {
                instance.Dispose();
            }
            catch
            {
                Debug.WriteLine($"Caught exception in EmbeddingProviderRegistry.cs");
                // Ignore disposal errors
            }
        }

        _instances.Clear();
        _registrations.Clear();
    }
}

/// <summary>
/// Registration information for a provider.
/// </summary>
internal sealed class ProviderRegistration
{
    /// <summary>The provider ID.</summary>
    public required string ProviderId { get; init; }

    /// <summary>The provider type.</summary>
    public required Type ProviderType { get; init; }

    /// <summary>Optional factory function.</summary>
    public Func<EmbeddingProviderConfig, IEmbeddingProvider>? Factory { get; init; }

    /// <summary>Pre-created instance (if any).</summary>
    public IEmbeddingProvider? Instance { get; init; }
}

/// <summary>
/// Information about a registered provider.
/// </summary>
public sealed record ProviderInfo
{
    /// <summary>The provider ID.</summary>
    public required string ProviderId { get; init; }

    /// <summary>The provider type.</summary>
    public required Type ProviderType { get; init; }

    /// <summary>Whether an instance exists.</summary>
    public bool HasInstance { get; init; }

    /// <summary>Vector dimensions (if instance exists).</summary>
    public int Dimensions { get; init; }

    /// <summary>Max tokens (if instance exists).</summary>
    public int MaxTokens { get; init; }

    /// <summary>Whether batch processing is supported.</summary>
    public bool SupportsMultipleTexts { get; init; }
}

/// <summary>
/// Aggregated metrics across all providers in the registry.
/// </summary>
public sealed class EmbeddingRegistryMetrics
{
    /// <summary>Number of active providers.</summary>
    public int ProviderCount { get; set; }

    /// <summary>Total requests across all providers.</summary>
    public long TotalRequests { get; set; }

    /// <summary>Successful requests across all providers.</summary>
    public long SuccessfulRequests { get; set; }

    /// <summary>Failed requests across all providers.</summary>
    public long FailedRequests { get; set; }

    /// <summary>Total tokens processed.</summary>
    public long TotalTokensProcessed { get; set; }

    /// <summary>Total embeddings generated.</summary>
    public long TotalEmbeddingsGenerated { get; set; }

    /// <summary>Total latency in milliseconds.</summary>
    public double TotalLatencyMs { get; set; }

    /// <summary>Estimated total cost in USD.</summary>
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>Total rate limit hits.</summary>
    public long RateLimitHits { get; set; }
}
