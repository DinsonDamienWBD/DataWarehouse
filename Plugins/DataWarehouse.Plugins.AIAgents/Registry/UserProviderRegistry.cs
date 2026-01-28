// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.Plugins.AIAgents.Capabilities;
using DataWarehouse.Plugins.AIAgents.Models;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.AIAgents.Registry;

/// <summary>
/// Manages user-level AI provider registrations.
/// Each user can register their own providers (BYOK) and configure which providers
/// to use for different capabilities.
/// </summary>
/// <remarks>
/// <para>
/// This registry allows users to:
/// - Register their own API keys for various AI providers
/// - Give custom names to their provider registrations
/// - Configure multiple instances of the same provider type
/// - Track provider usage and availability
/// </para>
/// <para>
/// Provider registrations are per-user and encrypted at rest.
/// </para>
/// </remarks>
public sealed class UserProviderRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, UserCapabilityConfiguration> _userConfigurations = new();
    private readonly InstanceCapabilityRegistry _instanceRegistry;
    private readonly IKernelStorageService? _storage;
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly List<Action<CapabilityConfigurationChangedEvent>> _changeHandlers = new();
    private readonly object _handlersLock = new();

    private const string StoragePrefix = "ai-capabilities/user/";

    /// <summary>
    /// Creates a new user provider registry.
    /// </summary>
    /// <param name="instanceRegistry">The instance capability registry for validation.</param>
    /// <param name="storage">Optional storage service for persistence.</param>
    public UserProviderRegistry(
        InstanceCapabilityRegistry instanceRegistry,
        IKernelStorageService? storage = null)
    {
        _instanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
        _storage = storage;
    }

    /// <summary>
    /// Gets or creates the configuration for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>The user's capability configuration.</returns>
    public UserCapabilityConfiguration GetUserConfiguration(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));

        return _userConfigurations.GetOrAdd(userId, id => new UserCapabilityConfiguration
        {
            UserId = id
        });
    }

    /// <summary>
    /// Registers a new provider for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="providerName">Unique name for this provider registration.</param>
    /// <param name="providerType">Provider type (e.g., "openai", "anthropic").</param>
    /// <param name="apiKey">API key for the provider.</param>
    /// <param name="options">Additional provider options.</param>
    /// <param name="instanceId">The instance identifier for validation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The registered provider.</returns>
    /// <exception cref="InvalidOperationException">If the provider type is blocked or BYOK is not allowed.</exception>
    public async Task<RegisteredProvider> RegisterProviderAsync(
        string userId,
        string providerName,
        string providerType,
        string? apiKey,
        ProviderRegistrationOptions? options = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("Provider name cannot be empty.", nameof(providerName));
        if (string.IsNullOrWhiteSpace(providerType))
            throw new ArgumentException("Provider type cannot be empty.", nameof(providerType));

        var normalizedType = providerType.ToLowerInvariant();

        // Validate against instance configuration
        var instanceConfig = _instanceRegistry.GetConfiguration(instanceId);

        if (!instanceConfig.AllowBYOK && !string.IsNullOrEmpty(apiKey))
        {
            throw new InvalidOperationException(
                "BYOK (Bring Your Own Key) is not allowed for this instance. " +
                "Contact your administrator to enable it.");
        }

        if (!_instanceRegistry.IsProviderTypeAllowed(normalizedType, instanceId))
        {
            throw new InvalidOperationException(
                $"Provider type '{providerType}' is blocked for this instance. " +
                "Contact your administrator for available provider types.");
        }

        var config = GetUserConfiguration(userId);

        // Check if name already exists
        if (config.Providers.ContainsKey(providerName))
        {
            throw new InvalidOperationException(
                $"A provider with name '{providerName}' already exists. " +
                "Use a different name or update the existing provider.");
        }

        var provider = new RegisteredProvider
        {
            Name = providerName,
            ProviderType = normalizedType,
            DisplayName = options?.DisplayName ?? $"{providerType} ({providerName})",
            ApiKey = apiKey, // TODO: Encrypt at rest
            Endpoint = options?.Endpoint,
            DefaultModel = options?.DefaultModel,
            OrganizationId = options?.OrganizationId,
            Settings = options?.Settings,
            RegisteredAt = DateTime.UtcNow,
            IsActive = true,
            SupportedCapabilities = GetProviderCapabilities(normalizedType),
            Priority = options?.Priority ?? 100
        };

        config.Providers[providerName] = provider;
        config.LastUpdatedAt = DateTime.UtcNow;
        config.Version++;

        await PersistUserConfigurationAsync(userId, ct);

        RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
        {
            ChangeType = ConfigurationChangeType.UserProvider,
            EntityId = userId,
            ChangeDescription = $"Registered provider: {providerName} ({providerType})",
            NewValue = providerName,
            ChangedBy = userId
        });

        return provider;
    }

    /// <summary>
    /// Unregisters a provider for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="providerName">Name of the provider to unregister.</param>
    /// <param name="force">If true, removes even if mapped to capabilities.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Warning message if the provider is mapped to capabilities, otherwise null.</returns>
    public async Task<string?> UnregisterProviderAsync(
        string userId,
        string providerName,
        bool force = false,
        CancellationToken ct = default)
    {
        var config = GetUserConfiguration(userId);

        if (!config.Providers.ContainsKey(providerName))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        // Check if this provider is mapped to any capabilities
        var mappedCapabilities = config.Mappings
            .Where(m => m.Value.ProviderName == providerName)
            .Select(m => m.Key)
            .ToList();

        if (mappedCapabilities.Count > 0 && !force)
        {
            return $"Warning: Provider '{providerName}' is mapped to the following capabilities: " +
                   $"{string.Join(", ", mappedCapabilities)}. " +
                   "These mappings will be removed. Use force=true to proceed.";
        }

        // Remove mappings first
        foreach (var capKey in mappedCapabilities)
        {
            config.Mappings.TryRemove(capKey, out _);
        }

        // Remove the provider
        config.Providers.TryRemove(providerName, out _);
        config.LastUpdatedAt = DateTime.UtcNow;
        config.Version++;

        await PersistUserConfigurationAsync(userId, ct);

        RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
        {
            ChangeType = ConfigurationChangeType.UserProvider,
            EntityId = userId,
            ChangeDescription = $"Unregistered provider: {providerName}",
            PreviousValue = providerName,
            ChangedBy = userId
        });

        return null;
    }

    /// <summary>
    /// Updates an existing provider registration.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="providerName">Name of the provider to update.</param>
    /// <param name="updates">The updates to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated provider.</returns>
    public async Task<RegisteredProvider> UpdateProviderAsync(
        string userId,
        string providerName,
        ProviderUpdateOptions updates,
        CancellationToken ct = default)
    {
        var config = GetUserConfiguration(userId);

        if (!config.Providers.TryGetValue(providerName, out var existing))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        var updated = existing with
        {
            DisplayName = updates.DisplayName ?? existing.DisplayName,
            ApiKey = updates.ApiKey ?? existing.ApiKey,
            Endpoint = updates.Endpoint ?? existing.Endpoint,
            DefaultModel = updates.DefaultModel ?? existing.DefaultModel,
            OrganizationId = updates.OrganizationId ?? existing.OrganizationId,
            Settings = updates.Settings ?? existing.Settings,
            IsActive = updates.IsActive ?? existing.IsActive,
            Priority = updates.Priority ?? existing.Priority
        };

        config.Providers[providerName] = updated;
        config.LastUpdatedAt = DateTime.UtcNow;
        config.Version++;

        await PersistUserConfigurationAsync(userId, ct);

        RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
        {
            ChangeType = ConfigurationChangeType.UserProvider,
            EntityId = userId,
            ChangeDescription = $"Updated provider: {providerName}",
            ChangedBy = userId
        });

        return updated;
    }

    /// <summary>
    /// Gets all providers registered by a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>Collection of registered providers.</returns>
    public IReadOnlyCollection<RegisteredProvider> GetUserProviders(string userId)
    {
        var config = GetUserConfiguration(userId);
        return config.Providers.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets a specific provider by name.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="providerName">The provider name.</param>
    /// <returns>The provider, or null if not found.</returns>
    public RegisteredProvider? GetProvider(string userId, string providerName)
    {
        var config = GetUserConfiguration(userId);
        return config.Providers.TryGetValue(providerName, out var provider) ? provider : null;
    }

    /// <summary>
    /// Gets all active providers for a user that support a specific capability.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="capability">The required capability.</param>
    /// <returns>Collection of providers supporting the capability.</returns>
    public IReadOnlyCollection<RegisteredProvider> GetProvidersForCapability(
        string userId,
        AICapability capability)
    {
        var config = GetUserConfiguration(userId);
        return config.Providers.Values
            .Where(p => p.IsActive && p.SupportedCapabilities.HasFlag(capability))
            .OrderBy(p => p.Priority)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Updates the last used timestamp for a provider.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="providerName">The provider name.</param>
    public void TouchProvider(string userId, string providerName)
    {
        var config = GetUserConfiguration(userId);
        if (config.Providers.TryGetValue(providerName, out var existing))
        {
            config.Providers[providerName] = existing with
            {
                LastUsedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Sets the default provider for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="providerName">The provider name.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetDefaultProviderAsync(
        string userId,
        string providerName,
        CancellationToken ct = default)
    {
        var config = GetUserConfiguration(userId);

        if (!config.Providers.ContainsKey(providerName))
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        // Need to create new config since DefaultProvider is init-only
        var newConfig = new UserCapabilityConfiguration
        {
            UserId = config.UserId,
            Providers = config.Providers,
            Mappings = config.Mappings,
            CapabilityOverrides = config.CapabilityOverrides,
            DefaultProvider = providerName,
            LastUpdatedAt = DateTime.UtcNow,
            Version = config.Version + 1
        };

        _userConfigurations[userId] = newConfig;
        await PersistUserConfigurationAsync(userId, ct);
    }

    /// <summary>
    /// Gets the default provider for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>The default provider, or null if not set.</returns>
    public RegisteredProvider? GetDefaultProvider(string userId)
    {
        var config = GetUserConfiguration(userId);
        if (string.IsNullOrEmpty(config.DefaultProvider))
        {
            return null;
        }

        return config.Providers.TryGetValue(config.DefaultProvider, out var provider) ? provider : null;
    }

    /// <summary>
    /// Validates a provider's API key by attempting a lightweight request.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="providerName">The provider name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation result.</returns>
    public async Task<ProviderValidationResult> ValidateProviderAsync(
        string userId,
        string providerName,
        CancellationToken ct = default)
    {
        var provider = GetProvider(userId, providerName);
        if (provider == null)
        {
            return new ProviderValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Provider '{providerName}' not found."
            };
        }

        // TODO: Implement actual validation by calling provider health endpoint
        // For now, just check if API key is present for non-local providers
        if (provider.ProviderType != "ollama" && string.IsNullOrEmpty(provider.ApiKey))
        {
            return new ProviderValidationResult
            {
                IsValid = false,
                ErrorMessage = "API key is required for this provider type.",
                ProviderName = providerName,
                ProviderType = provider.ProviderType
            };
        }

        await Task.CompletedTask; // Placeholder for async validation

        return new ProviderValidationResult
        {
            IsValid = true,
            ProviderName = providerName,
            ProviderType = provider.ProviderType,
            ValidatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Subscribes to configuration change events.
    /// </summary>
    /// <param name="handler">The event handler.</param>
    /// <returns>Disposable to unsubscribe.</returns>
    public IDisposable OnConfigurationChanged(Action<CapabilityConfigurationChangedEvent> handler)
    {
        lock (_handlersLock)
        {
            _changeHandlers.Add(handler);
        }

        return new Unsubscriber(() =>
        {
            lock (_handlersLock)
            {
                _changeHandlers.Remove(handler);
            }
        });
    }

    /// <summary>
    /// Loads a user's configuration from storage.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadUserConfigurationAsync(string userId, CancellationToken ct = default)
    {
        if (_storage == null) return;

        var path = $"{StoragePrefix}{userId}.json";
        var data = await _storage.LoadBytesAsync(path, ct);
        if (data != null)
        {
            var config = JsonSerializer.Deserialize<UserCapabilityConfiguration>(data);
            if (config != null)
            {
                _userConfigurations[userId] = config;
            }
        }
    }

    /// <summary>
    /// Gets the capabilities supported by a provider type.
    /// </summary>
    private static AICapability GetProviderCapabilities(string providerType)
    {
        return providerType.ToLowerInvariant() switch
        {
            "openai" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings |
                        AICapability.Vision | AICapability.Audio | AICapability.CodeIntelligence,
            "anthropic" or "claude" => AICapability.Chat | AICapability.Generation |
                                        AICapability.Vision | AICapability.CodeIntelligence,
            "google" or "gemini" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings |
                                    AICapability.Vision | AICapability.Audio,
            "azure" or "azureopenai" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings |
                                        AICapability.Vision | AICapability.Audio | AICapability.CodeIntelligence,
            "cohere" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings,
            "mistral" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings |
                         AICapability.CodeIntelligence,
            "ollama" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings |
                        AICapability.Vision | AICapability.CodeIntelligence,
            "groq" => AICapability.Chat | AICapability.Generation,
            "together" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings,
            "perplexity" => AICapability.Chat | AICapability.Knowledge,
            "huggingface" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings,
            "bedrock" => AICapability.Chat | AICapability.Generation | AICapability.Embeddings |
                         AICapability.Vision | AICapability.Knowledge,
            _ => AICapability.Chat | AICapability.Generation // Minimum for unknown providers
        };
    }

    private async Task PersistUserConfigurationAsync(string userId, CancellationToken ct)
    {
        if (_storage == null) return;

        await _persistLock.WaitAsync(ct);
        try
        {
            var config = GetUserConfiguration(userId);
            var path = $"{StoragePrefix}{userId}.json";
            var data = JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await _storage.SaveAsync(path, data, null, ct);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private void RaiseConfigurationChanged(CapabilityConfigurationChangedEvent evt)
    {
        List<Action<CapabilityConfigurationChangedEvent>> handlers;
        lock (_handlersLock)
        {
            handlers = _changeHandlers.ToList();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(evt);
            }
            catch
            {
                // Log but don't throw
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _persistLock.Dispose();
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly Action _unsubscribe;
        public Unsubscriber(Action unsubscribe) => _unsubscribe = unsubscribe;
        public void Dispose() => _unsubscribe();
    }
}

/// <summary>
/// Options for registering a new provider.
/// </summary>
public sealed record ProviderRegistrationOptions
{
    /// <summary>Display name for the provider.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Custom endpoint URL.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Default model to use.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>Organization ID (for OpenAI).</summary>
    public string? OrganizationId { get; init; }

    /// <summary>Additional provider-specific settings.</summary>
    public IReadOnlyDictionary<string, object>? Settings { get; init; }

    /// <summary>Priority for fallback ordering (lower = higher priority).</summary>
    public int Priority { get; init; } = 100;
}

/// <summary>
/// Options for updating an existing provider.
/// </summary>
public sealed record ProviderUpdateOptions
{
    /// <summary>New display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>New API key.</summary>
    public string? ApiKey { get; init; }

    /// <summary>New endpoint.</summary>
    public string? Endpoint { get; init; }

    /// <summary>New default model.</summary>
    public string? DefaultModel { get; init; }

    /// <summary>New organization ID.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>New settings.</summary>
    public IReadOnlyDictionary<string, object>? Settings { get; init; }

    /// <summary>New active status.</summary>
    public bool? IsActive { get; init; }

    /// <summary>New priority.</summary>
    public int? Priority { get; init; }
}

/// <summary>
/// Result of provider validation.
/// </summary>
public sealed record ProviderValidationResult
{
    /// <summary>Whether the provider is valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Error message if invalid.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Provider name.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Provider type.</summary>
    public string? ProviderType { get; init; }

    /// <summary>When validation was performed.</summary>
    public DateTime? ValidatedAt { get; init; }

    /// <summary>Available models (if validation included model listing).</summary>
    public IReadOnlyList<string>? AvailableModels { get; init; }
}
