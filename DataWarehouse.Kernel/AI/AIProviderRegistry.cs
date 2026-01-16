using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.AI
{
    /// <summary>
    /// Default implementation of the AI Provider Registry.
    /// Manages registration, discovery, and selection of AI providers.
    /// </summary>
    public sealed class AIProviderRegistry : IAIProviderRegistry
    {
        private readonly ConcurrentDictionary<string, IAIProvider> _providers = new();
        private readonly object _defaultLock = new();
        private string? _defaultProviderId;

        /// <summary>
        /// Event raised when a provider is registered.
        /// </summary>
        public event Action<IAIProvider>? OnProviderRegistered;

        /// <summary>
        /// Event raised when a provider is unregistered.
        /// </summary>
        public event Action<string>? OnProviderUnregistered;

        public void Register(IAIProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);

            if (string.IsNullOrEmpty(provider.ProviderId))
            {
                throw new ArgumentException("Provider must have a valid ProviderId", nameof(provider));
            }

            _providers[provider.ProviderId] = provider;

            // Set as default if it's the first provider
            lock (_defaultLock)
            {
                if (_defaultProviderId == null)
                {
                    _defaultProviderId = provider.ProviderId;
                }
            }

            OnProviderRegistered?.Invoke(provider);
        }

        public void Unregister(string providerId)
        {
            ArgumentNullException.ThrowIfNull(providerId);

            if (_providers.TryRemove(providerId, out _))
            {
                // If this was the default, pick a new one
                lock (_defaultLock)
                {
                    if (_defaultProviderId == providerId)
                    {
                        _defaultProviderId = _providers.Keys.FirstOrDefault();
                    }
                }

                OnProviderUnregistered?.Invoke(providerId);
            }
        }

        public IAIProvider? GetProvider(string providerId)
        {
            ArgumentNullException.ThrowIfNull(providerId);

            return _providers.TryGetValue(providerId, out var provider) ? provider : null;
        }

        public IEnumerable<IAIProvider> GetAllProviders()
        {
            return _providers.Values.ToArray();
        }

        public IAIProvider? GetDefaultProvider()
        {
            lock (_defaultLock)
            {
                if (_defaultProviderId == null)
                {
                    return null;
                }

                return _providers.TryGetValue(_defaultProviderId, out var provider) ? provider : null;
            }
        }

        public void SetDefaultProvider(string providerId)
        {
            ArgumentNullException.ThrowIfNull(providerId);

            if (!_providers.ContainsKey(providerId))
            {
                throw new ArgumentException($"Provider '{providerId}' is not registered", nameof(providerId));
            }

            lock (_defaultLock)
            {
                _defaultProviderId = providerId;
            }
        }

        public IEnumerable<IAIProvider> GetProvidersWithCapabilities(AICapabilities required)
        {
            return _providers.Values
                .Where(p => p.IsAvailable && (p.Capabilities & required) == required)
                .ToArray();
        }

        /// <summary>
        /// Gets the best available provider for the specified capabilities.
        /// Returns available providers sorted by capability match.
        /// </summary>
        public IAIProvider? GetBestProvider(AICapabilities required)
        {
            var candidates = GetProvidersWithCapabilities(required).ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            // Prefer the default provider if it meets requirements
            var defaultProvider = GetDefaultProvider();
            if (defaultProvider != null &&
                defaultProvider.IsAvailable &&
                (defaultProvider.Capabilities & required) == required)
            {
                return defaultProvider;
            }

            // Otherwise return the first available
            return candidates.FirstOrDefault();
        }

        /// <summary>
        /// Gets a provider for embeddings, or null if none available.
        /// </summary>
        public IAIProvider? GetEmbeddingProvider()
        {
            return GetBestProvider(AICapabilities.Embeddings);
        }

        /// <summary>
        /// Gets a provider for text completion, or null if none available.
        /// </summary>
        public IAIProvider? GetCompletionProvider()
        {
            return GetBestProvider(AICapabilities.TextCompletion);
        }

        /// <summary>
        /// Gets a provider for chat completion, or null if none available.
        /// </summary>
        public IAIProvider? GetChatProvider()
        {
            return GetBestProvider(AICapabilities.ChatCompletion);
        }

        /// <summary>
        /// Gets a provider for streaming completion, or null if none available.
        /// </summary>
        public IAIProvider? GetStreamingProvider()
        {
            return GetBestProvider(AICapabilities.ChatCompletion | AICapabilities.Streaming);
        }

        /// <summary>
        /// Gets a provider chain for fallback scenarios.
        /// Returns providers in priority order that support the required capabilities.
        /// </summary>
        public IEnumerable<IAIProvider> GetProviderChain(AICapabilities required)
        {
            var providers = GetProvidersWithCapabilities(required).ToList();

            // Default provider first
            var defaultProvider = GetDefaultProvider();
            if (defaultProvider != null &&
                defaultProvider.IsAvailable &&
                (defaultProvider.Capabilities & required) == required)
            {
                yield return defaultProvider;
                providers.Remove(defaultProvider);
            }

            foreach (var provider in providers)
            {
                yield return provider;
            }
        }

        /// <summary>
        /// Gets provider availability summary.
        /// </summary>
        public AIProviderSummary GetSummary()
        {
            var providers = _providers.Values.ToList();

            return new AIProviderSummary
            {
                TotalProviders = providers.Count,
                AvailableProviders = providers.Count(p => p.IsAvailable),
                DefaultProviderId = _defaultProviderId,
                ProviderDetails = providers.Select(p => new AIProviderDetail
                {
                    ProviderId = p.ProviderId,
                    DisplayName = p.DisplayName,
                    IsAvailable = p.IsAvailable,
                    Capabilities = p.Capabilities,
                    IsDefault = p.ProviderId == _defaultProviderId
                }).ToList()
            };
        }
    }

    /// <summary>
    /// Summary of registered AI providers.
    /// </summary>
    public class AIProviderSummary
    {
        public int TotalProviders { get; init; }
        public int AvailableProviders { get; init; }
        public string? DefaultProviderId { get; init; }
        public List<AIProviderDetail> ProviderDetails { get; init; } = new();
    }

    /// <summary>
    /// Details of a single AI provider.
    /// </summary>
    public class AIProviderDetail
    {
        public string ProviderId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public bool IsAvailable { get; init; }
        public AICapabilities Capabilities { get; init; }
        public bool IsDefault { get; init; }
    }
}
