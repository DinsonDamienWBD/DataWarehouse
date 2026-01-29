// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.Plugins.AIAgents.Capabilities;
using DataWarehouse.Plugins.AIAgents.Models;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.AIAgents.Registry;

/// <summary>
/// Manages user-level capability-to-provider mappings.
/// Allows users to specify which of their registered providers handles each capability.
/// </summary>
/// <remarks>
/// <para>
/// This registry provides:
/// - Per-capability provider mapping (e.g., use Claude for chat, OpenAI for embeddings)
/// - Per-sub-capability overrides for fine-grained control
/// - Model overrides per capability
/// - Graceful fallback when mappings are incomplete
/// </para>
/// <para>
/// Mappings are validated against:
/// - Instance-level capability enablement (from InstanceCapabilityRegistry)
/// - User's registered providers (from UserProviderRegistry)
/// </para>
/// </remarks>
public sealed class UserCapabilityMappings : IDisposable
{
    private readonly ConcurrentDictionary<string, UserMappingConfiguration> _userMappings = new();
    private readonly InstanceCapabilityRegistry _instanceRegistry;
    private readonly UserProviderRegistry _providerRegistry;
    private readonly IKernelStorageService? _storage;
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly List<Action<CapabilityConfigurationChangedEvent>> _changeHandlers = new();
    private readonly object _handlersLock = new();

    private const string StoragePrefix = "ai-capabilities/mappings/";

    /// <summary>
    /// Creates a new user capability mappings registry.
    /// </summary>
    /// <param name="instanceRegistry">The instance capability registry for validation.</param>
    /// <param name="providerRegistry">The user provider registry for provider resolution.</param>
    /// <param name="storage">Optional storage service for persistence.</param>
    public UserCapabilityMappings(
        InstanceCapabilityRegistry instanceRegistry,
        UserProviderRegistry providerRegistry,
        IKernelStorageService? storage = null)
    {
        _instanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _storage = storage;
    }

    /// <summary>
    /// Gets or creates the mapping configuration for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>The user's mapping configuration.</returns>
    private UserMappingConfiguration GetUserMappings(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));

        return _userMappings.GetOrAdd(userId, id => new UserMappingConfiguration { UserId = id });
    }

    /// <summary>
    /// Sets a capability-to-provider mapping for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="capability">The capability to map.</param>
    /// <param name="providerName">The name of the registered provider to use.</param>
    /// <param name="modelOverride">Optional model to use (overrides provider default).</param>
    /// <param name="instanceId">The instance identifier for validation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created mapping.</returns>
    /// <exception cref="InvalidOperationException">If the capability is disabled or provider not found.</exception>
    public async Task<CapabilityMapping> SetMappingAsync(
        string userId,
        AICapability capability,
        string providerName,
        string? modelOverride = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        // Validate capability is enabled at instance level
        if (!_instanceRegistry.IsCapabilityEnabled(capability, instanceId))
        {
            throw new InvalidOperationException(
                $"Capability '{capability}' is disabled for this instance. " +
                "Contact your administrator to enable it.");
        }

        // Validate provider exists and is registered for this user
        var provider = _providerRegistry.GetProvider(userId, providerName);
        if (provider == null)
        {
            throw new InvalidOperationException(
                $"Provider '{providerName}' is not registered for user '{userId}'. " +
                "Register the provider first using RegisterProvider.");
        }

        // Validate provider supports the capability
        if (!provider.SupportedCapabilities.HasFlag(capability))
        {
            throw new InvalidOperationException(
                $"Provider '{providerName}' does not support capability '{capability}'. " +
                $"Supported capabilities: {provider.SupportedCapabilities}");
        }

        var mappings = GetUserMappings(userId);
        var key = GetCapabilityKey(capability, null);

        var mapping = new CapabilityMapping
        {
            Capability = capability,
            SubCapability = null,
            ProviderName = providerName,
            ModelOverride = modelOverride,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        var previousMapping = mappings.CapabilityMappings.TryGetValue(key, out var prev) ? prev : null;
        mappings.CapabilityMappings[key] = mapping;
        mappings.LastUpdatedAt = DateTime.UtcNow;
        mappings.Version++;

        await PersistUserMappingsAsync(userId, ct);

        RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
        {
            ChangeType = ConfigurationChangeType.UserMapping,
            EntityId = userId,
            ChangeDescription = $"Set mapping for {capability} -> {providerName}",
            PreviousValue = previousMapping?.ProviderName,
            NewValue = providerName,
            ChangedBy = userId
        });

        return mapping;
    }

    /// <summary>
    /// Sets a sub-capability-to-provider mapping for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="subCapability">The sub-capability to map.</param>
    /// <param name="providerName">The name of the registered provider to use.</param>
    /// <param name="modelOverride">Optional model to use (overrides provider default).</param>
    /// <param name="instanceId">The instance identifier for validation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created mapping.</returns>
    public async Task<CapabilityMapping> SetSubCapabilityMappingAsync(
        string userId,
        AISubCapability subCapability,
        string providerName,
        string? modelOverride = null,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        var parentCapability = subCapability.GetParentCapability();

        // Validate sub-capability is enabled at instance level
        if (!_instanceRegistry.IsSubCapabilityEnabled(subCapability, instanceId))
        {
            throw new InvalidOperationException(
                $"Sub-capability '{subCapability}' is disabled for this instance.");
        }

        // Validate provider exists and supports the parent capability
        var provider = _providerRegistry.GetProvider(userId, providerName);
        if (provider == null)
        {
            throw new InvalidOperationException(
                $"Provider '{providerName}' is not registered for user '{userId}'.");
        }

        if (!provider.SupportedCapabilities.HasFlag(parentCapability))
        {
            throw new InvalidOperationException(
                $"Provider '{providerName}' does not support capability '{parentCapability}'.");
        }

        var mappings = GetUserMappings(userId);
        var key = GetCapabilityKey(parentCapability, subCapability);

        var mapping = new CapabilityMapping
        {
            Capability = parentCapability,
            SubCapability = subCapability,
            ProviderName = providerName,
            ModelOverride = modelOverride,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        mappings.CapabilityMappings[key] = mapping;
        mappings.LastUpdatedAt = DateTime.UtcNow;
        mappings.Version++;

        await PersistUserMappingsAsync(userId, ct);

        RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
        {
            ChangeType = ConfigurationChangeType.UserMapping,
            EntityId = userId,
            ChangeDescription = $"Set mapping for {subCapability} -> {providerName}",
            NewValue = providerName,
            ChangedBy = userId
        });

        return mapping;
    }

    /// <summary>
    /// Gets the mapping for a capability.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="capability">The capability to get the mapping for.</param>
    /// <returns>The provider name, or null if not mapped.</returns>
    public string? GetMapping(string userId, AICapability capability)
    {
        var mappings = GetUserMappings(userId);
        var key = GetCapabilityKey(capability, null);

        if (mappings.CapabilityMappings.TryGetValue(key, out var mapping) && mapping.IsActive)
        {
            return mapping.ProviderName;
        }

        return null;
    }

    /// <summary>
    /// Gets the mapping for a sub-capability.
    /// Falls back to the parent capability mapping if no specific mapping exists.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="subCapability">The sub-capability to get the mapping for.</param>
    /// <returns>The provider name, or null if not mapped.</returns>
    public string? GetSubCapabilityMapping(string userId, AISubCapability subCapability)
    {
        var mappings = GetUserMappings(userId);
        var parentCapability = subCapability.GetParentCapability();

        // First check for specific sub-capability mapping
        var subKey = GetCapabilityKey(parentCapability, subCapability);
        if (mappings.CapabilityMappings.TryGetValue(subKey, out var subMapping) && subMapping.IsActive)
        {
            return subMapping.ProviderName;
        }

        // Fall back to parent capability mapping
        return GetMapping(userId, parentCapability);
    }

    /// <summary>
    /// Removes a capability mapping for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="capability">The capability to remove the mapping for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the mapping was removed.</returns>
    public async Task<bool> RemoveMappingAsync(
        string userId,
        AICapability capability,
        CancellationToken ct = default)
    {
        var mappings = GetUserMappings(userId);
        var key = GetCapabilityKey(capability, null);

        var removed = mappings.CapabilityMappings.TryRemove(key, out var removedMapping);
        if (removed)
        {
            mappings.LastUpdatedAt = DateTime.UtcNow;
            mappings.Version++;

            await PersistUserMappingsAsync(userId, ct);

            RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
            {
                ChangeType = ConfigurationChangeType.UserMapping,
                EntityId = userId,
                ChangeDescription = $"Removed mapping for {capability}",
                PreviousValue = removedMapping?.ProviderName,
                ChangedBy = userId
            });
        }

        return removed;
    }

    /// <summary>
    /// Resolves the provider for a capability request.
    /// Returns the actual provider or a graceful error result.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="capability">The capability being requested.</param>
    /// <param name="subCapability">Optional sub-capability for more specific resolution.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>Resolution result with provider or error information.</returns>
    public ProviderResolutionResult ResolveProvider(
        string userId,
        AICapability capability,
        AISubCapability? subCapability = null,
        string? instanceId = null)
    {
        // Check instance-level capability enablement
        if (!_instanceRegistry.IsCapabilityEnabled(capability, instanceId))
        {
            return ProviderResolutionResult.Failed(
                ProviderResolutionError.CapabilityDisabled,
                $"Capability '{capability}' is disabled for this instance.");
        }

        // Check sub-capability if specified
        if (subCapability.HasValue && !_instanceRegistry.IsSubCapabilityEnabled(subCapability.Value, instanceId))
        {
            return ProviderResolutionResult.Failed(
                ProviderResolutionError.CapabilityDisabled,
                $"Sub-capability '{subCapability}' is disabled for this instance.");
        }

        // Try to get the mapping
        string? providerName;
        if (subCapability.HasValue)
        {
            providerName = GetSubCapabilityMapping(userId, subCapability.Value);
        }
        else
        {
            providerName = GetMapping(userId, capability);
        }

        // If no specific mapping, try user's default provider
        if (string.IsNullOrEmpty(providerName))
        {
            var defaultProvider = _providerRegistry.GetDefaultProvider(userId);
            if (defaultProvider != null && defaultProvider.SupportedCapabilities.HasFlag(capability))
            {
                providerName = defaultProvider.Name;
            }
        }

        // If still no provider, try instance default
        if (string.IsNullOrEmpty(providerName))
        {
            var instanceConfig = _instanceRegistry.GetConfiguration(instanceId);
            if (!string.IsNullOrEmpty(instanceConfig.DefaultSystemProvider))
            {
                // Check if this system provider is registered for the user
                var systemProvider = _providerRegistry.GetProvider(userId, instanceConfig.DefaultSystemProvider);
                if (systemProvider != null && systemProvider.SupportedCapabilities.HasFlag(capability))
                {
                    providerName = systemProvider.Name;
                }
            }
        }

        // No provider found
        if (string.IsNullOrEmpty(providerName))
        {
            return ProviderResolutionResult.Failed(
                ProviderResolutionError.NoProviderConfigured,
                $"No provider configured for capability '{capability}'. " +
                "Register a provider and set up a mapping.");
        }

        // Get the actual provider
        var provider = _providerRegistry.GetProvider(userId, providerName);
        if (provider == null)
        {
            return ProviderResolutionResult.Failed(
                ProviderResolutionError.ProviderUnavailable,
                $"Provider '{providerName}' is not available.");
        }

        if (!provider.IsActive)
        {
            return ProviderResolutionResult.Failed(
                ProviderResolutionError.ProviderUnavailable,
                $"Provider '{providerName}' is currently inactive.");
        }

        // Check if provider type is allowed at instance level
        if (!_instanceRegistry.IsProviderTypeAllowed(provider.ProviderType, instanceId))
        {
            return ProviderResolutionResult.Failed(
                ProviderResolutionError.ProviderTypeBlocked,
                $"Provider type '{provider.ProviderType}' is blocked for this instance.");
        }

        // Get model override from mapping if available
        var mappings = GetUserMappings(userId);
        var key = subCapability.HasValue
            ? GetCapabilityKey(capability, subCapability)
            : GetCapabilityKey(capability, null);

        string? model = provider.DefaultModel;
        if (mappings.CapabilityMappings.TryGetValue(key, out var mapping) && !string.IsNullOrEmpty(mapping.ModelOverride))
        {
            model = mapping.ModelOverride;
        }

        // Update provider last used timestamp
        _providerRegistry.TouchProvider(userId, providerName);

        return ProviderResolutionResult.Resolved(provider, model);
    }

    /// <summary>
    /// Resolves the provider and returns a graceful CapabilityResult for use in capability operations.
    /// </summary>
    /// <typeparam name="T">The type of result data.</typeparam>
    /// <param name="userId">The user identifier.</param>
    /// <param name="capability">The capability being requested.</param>
    /// <param name="subCapability">Optional sub-capability.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>CapabilityResult with either the provider or error information.</returns>
    public CapabilityResult<RegisteredProvider> ResolveProviderAsCapabilityResult(
        string userId,
        AICapability capability,
        AISubCapability? subCapability = null,
        string? instanceId = null)
    {
        var resolution = ResolveProvider(userId, capability, subCapability, instanceId);

        if (!resolution.Success)
        {
            return resolution.ErrorCode switch
            {
                ProviderResolutionError.CapabilityDisabled =>
                    CapabilityResult<RegisteredProvider>.Disabled(capability.ToString()),
                ProviderResolutionError.NoProviderConfigured =>
                    CapabilityResult<RegisteredProvider>.NoProvider(capability.ToString()),
                _ => CapabilityResult<RegisteredProvider>.Fail(
                    resolution.ErrorMessage ?? "Unknown error",
                    resolution.ErrorCode?.ToString())
            };
        }

        return CapabilityResult<RegisteredProvider>.Ok(
            resolution.Provider!,
            resolution.Provider!.Name,
            resolution.Model);
    }

    /// <summary>
    /// Gets all mappings for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>Collection of all capability mappings.</returns>
    public IReadOnlyCollection<CapabilityMapping> GetAllMappings(string userId)
    {
        var mappings = GetUserMappings(userId);
        return mappings.CapabilityMappings.Values
            .Where(m => m.IsActive)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Clears all mappings for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ClearAllMappingsAsync(string userId, CancellationToken ct = default)
    {
        var mappings = GetUserMappings(userId);
        var count = mappings.CapabilityMappings.Count;

        mappings.CapabilityMappings.Clear();
        mappings.LastUpdatedAt = DateTime.UtcNow;
        mappings.Version++;

        await PersistUserMappingsAsync(userId, ct);

        RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
        {
            ChangeType = ConfigurationChangeType.UserMapping,
            EntityId = userId,
            ChangeDescription = $"Cleared all {count} capability mappings",
            ChangedBy = userId
        });
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
    /// Loads a user's mappings from storage.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadUserMappingsAsync(string userId, CancellationToken ct = default)
    {
        if (_storage == null) return;

        var path = $"{StoragePrefix}{userId}.json";
        var data = await _storage.LoadBytesAsync(path, ct);
        if (data != null)
        {
            var mappings = JsonSerializer.Deserialize<UserMappingConfiguration>(data);
            if (mappings != null)
            {
                _userMappings[userId] = mappings;
            }
        }
    }

    /// <summary>
    /// Gets the key for a capability or sub-capability mapping.
    /// </summary>
    private static string GetCapabilityKey(AICapability capability, AISubCapability? subCapability)
    {
        return subCapability.HasValue
            ? $"{capability}.{subCapability.Value}"
            : capability.ToString();
    }

    /// <summary>
    /// Persists user mappings to storage.
    /// </summary>
    private async Task PersistUserMappingsAsync(string userId, CancellationToken ct)
    {
        if (_storage == null) return;

        await _persistLock.WaitAsync(ct);
        try
        {
            var mappings = GetUserMappings(userId);
            var path = $"{StoragePrefix}{userId}.json";
            var data = JsonSerializer.SerializeToUtf8Bytes(mappings, new JsonSerializerOptions
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
/// Internal configuration for user capability mappings.
/// </summary>
internal sealed class UserMappingConfiguration
{
    /// <summary>User identifier.</summary>
    public required string UserId { get; init; }

    /// <summary>Capability-to-provider mappings.</summary>
    public ConcurrentDictionary<string, CapabilityMapping> CapabilityMappings { get; init; } = new();

    /// <summary>When this configuration was last updated.</summary>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Configuration version for optimistic concurrency.</summary>
    public long Version { get; set; } = 1;
}
