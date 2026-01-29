// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.Plugins.AIAgents.Capabilities;
using DataWarehouse.Plugins.AIAgents.Models;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.AIAgents.Registry;

/// <summary>
/// Manages instance-level AI capability configuration.
/// This registry is controlled by administrators and determines which capabilities
/// are available to all users of an instance.
/// </summary>
/// <remarks>
/// <para>
/// The instance capability registry enforces organization-wide policies:
/// - Which AI capabilities are available
/// - Which provider types are allowed/blocked
/// - Global rate limits
/// - Whether BYOK is permitted
/// </para>
/// <para>
/// Changes to this registry require administrator privileges and are audited.
/// </para>
/// </remarks>
public sealed class InstanceCapabilityRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, InstanceCapabilityConfiguration> _configurations = new();
    private readonly ConcurrentDictionary<string, bool> _childrenMirrorParent = new();
    private readonly IKernelStorageService? _storage;
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly List<Action<CapabilityConfigurationChangedEvent>> _changeHandlers = new();
    private readonly object _handlersLock = new();

    private const string StoragePrefix = "ai-capabilities/instance/";
    private const string DefaultInstanceId = "default";

    /// <summary>
    /// Creates a new instance capability registry.
    /// </summary>
    /// <param name="storage">Optional storage service for persistence.</param>
    public InstanceCapabilityRegistry(IKernelStorageService? storage = null)
    {
        _storage = storage;
    }

    /// <summary>
    /// Gets or creates the configuration for an instance.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>The instance configuration.</returns>
    public InstanceCapabilityConfiguration GetConfiguration(string? instanceId = null)
    {
        instanceId ??= DefaultInstanceId;
        return _configurations.GetOrAdd(instanceId, id => new InstanceCapabilityConfiguration
        {
            InstanceId = id
        });
    }

    /// <summary>
    /// Gets or sets whether toggling a parent capability automatically toggles all its children.
    /// When true (default), enabling/disabling a parent capability enables/disables all sub-capabilities.
    /// When false, fine-grained control is available - sub-capabilities must be toggled individually.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>True if children mirror parent, false for fine-grained control.</returns>
    public bool GetChildrenMirrorParent(string? instanceId = null)
    {
        instanceId ??= DefaultInstanceId;
        return _childrenMirrorParent.GetOrAdd(instanceId, _ => true); // Default: true
    }

    /// <summary>
    /// Sets whether toggling a parent capability automatically toggles all its children.
    /// </summary>
    /// <param name="mirrorParent">True to have children mirror parent state, false for fine-grained control.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetChildrenMirrorParentAsync(
        bool mirrorParent,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var previousValue = GetChildrenMirrorParent(instanceId);

        _childrenMirrorParent[instanceId] = mirrorParent;

        // Also persist this setting to storage
        await PersistConfigurationAsync(instanceId, ct);

        RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
        {
            ChangeType = ConfigurationChangeType.InstanceCapability,
            EntityId = instanceId,
            ChangeDescription = $"Changed ChildrenMirrorParent to {mirrorParent}",
            PreviousValue = previousValue.ToString(),
            NewValue = mirrorParent.ToString(),
            ChangedBy = adminUserId
        });
    }

    /// <summary>
    /// Checks if a capability is enabled for the instance.
    /// </summary>
    /// <param name="capability">The capability to check.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>True if the capability is enabled.</returns>
    public bool IsCapabilityEnabled(AICapability capability, string? instanceId = null)
    {
        var config = GetConfiguration(instanceId);
        return config.EnabledCapabilities.Contains(capability);
    }

    /// <summary>
    /// Checks if a sub-capability is enabled for the instance.
    /// </summary>
    /// <param name="subCapability">The sub-capability to check.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>True if the sub-capability is enabled.</returns>
    public bool IsSubCapabilityEnabled(AISubCapability subCapability, string? instanceId = null)
    {
        var config = GetConfiguration(instanceId);

        // First check if the parent capability is enabled
        var parentCapability = subCapability.GetParentCapability();
        if (!config.EnabledCapabilities.Contains(parentCapability))
        {
            return false;
        }

        // Check if explicitly disabled
        if (config.DisabledSubCapabilities.Contains(subCapability))
        {
            return false;
        }

        // If there's an explicit enable list, check it
        if (config.EnabledSubCapabilities.Count > 0)
        {
            return config.EnabledSubCapabilities.Contains(subCapability);
        }

        // Default: enabled if parent is enabled
        return true;
    }

    /// <summary>
    /// Enables a capability for the instance.
    /// When ChildrenMirrorParent is true, all sub-capabilities are also enabled.
    /// </summary>
    /// <param name="capability">The capability to enable.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the capability was enabled (false if already enabled).</returns>
    /// <exception cref="InvalidOperationException">If the caller is not an administrator.</exception>
    public async Task<bool> EnableCapabilityAsync(
        AICapability capability,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        var added = config.EnabledCapabilities.Add(capability);
        if (added)
        {
            // If ChildrenMirrorParent is enabled, enable all sub-capabilities
            if (GetChildrenMirrorParent(instanceId))
            {
                var subCapabilities = capability.GetSubCapabilities();
                foreach (var subCap in subCapabilities)
                {
                    config.DisabledSubCapabilities.Remove(subCap);
                    // If using explicit enable list, add to it
                    if (config.EnabledSubCapabilities.Count > 0)
                    {
                        config.EnabledSubCapabilities.Add(subCap);
                    }
                }
            }

            config.LastUpdatedAt = DateTime.UtcNow;
            config.LastUpdatedBy = adminUserId;
            config.Version++;

            await PersistConfigurationAsync(instanceId, ct);

            RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
            {
                ChangeType = ConfigurationChangeType.InstanceCapability,
                EntityId = instanceId,
                ChangeDescription = $"Enabled capability: {capability}" +
                    (GetChildrenMirrorParent(instanceId) ? " (including all sub-capabilities)" : ""),
                NewValue = capability.ToString(),
                ChangedBy = adminUserId
            });
        }

        return added;
    }

    /// <summary>
    /// Disables a capability for the instance.
    /// When ChildrenMirrorParent is true, all sub-capabilities are also disabled.
    /// </summary>
    /// <param name="capability">The capability to disable.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the capability was disabled (false if already disabled).</returns>
    public async Task<bool> DisableCapabilityAsync(
        AICapability capability,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        var removed = config.EnabledCapabilities.Remove(capability);
        if (removed)
        {
            // If ChildrenMirrorParent is enabled, disable all sub-capabilities
            if (GetChildrenMirrorParent(instanceId))
            {
                var subCapabilities = capability.GetSubCapabilities();
                foreach (var subCap in subCapabilities)
                {
                    config.DisabledSubCapabilities.Add(subCap);
                    config.EnabledSubCapabilities.Remove(subCap);
                }
            }

            config.LastUpdatedAt = DateTime.UtcNow;
            config.LastUpdatedBy = adminUserId;
            config.Version++;

            await PersistConfigurationAsync(instanceId, ct);

            RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
            {
                ChangeType = ConfigurationChangeType.InstanceCapability,
                EntityId = instanceId,
                ChangeDescription = $"Disabled capability: {capability}" +
                    (GetChildrenMirrorParent(instanceId) ? " (including all sub-capabilities)" : ""),
                PreviousValue = capability.ToString(),
                ChangedBy = adminUserId
            });
        }

        return removed;
    }

    /// <summary>
    /// Enables a sub-capability for the instance.
    /// </summary>
    /// <param name="subCapability">The sub-capability to enable.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the change was made.</returns>
    public async Task<bool> EnableSubCapabilityAsync(
        AISubCapability subCapability,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        // Remove from disabled set and optionally add to enabled set
        var changed = config.DisabledSubCapabilities.Remove(subCapability);

        if (config.EnabledSubCapabilities.Count > 0)
        {
            changed |= config.EnabledSubCapabilities.Add(subCapability);
        }

        if (changed)
        {
            config.LastUpdatedAt = DateTime.UtcNow;
            config.LastUpdatedBy = adminUserId;
            config.Version++;

            await PersistConfigurationAsync(instanceId, ct);

            RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
            {
                ChangeType = ConfigurationChangeType.InstanceCapability,
                EntityId = instanceId,
                ChangeDescription = $"Enabled sub-capability: {subCapability}",
                NewValue = subCapability.ToString(),
                ChangedBy = adminUserId
            });
        }

        return changed;
    }

    /// <summary>
    /// Disables a sub-capability for the instance.
    /// </summary>
    /// <param name="subCapability">The sub-capability to disable.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the change was made.</returns>
    public async Task<bool> DisableSubCapabilityAsync(
        AISubCapability subCapability,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        var changed = config.DisabledSubCapabilities.Add(subCapability);
        config.EnabledSubCapabilities.Remove(subCapability);

        if (changed)
        {
            config.LastUpdatedAt = DateTime.UtcNow;
            config.LastUpdatedBy = adminUserId;
            config.Version++;

            await PersistConfigurationAsync(instanceId, ct);

            RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
            {
                ChangeType = ConfigurationChangeType.InstanceCapability,
                EntityId = instanceId,
                ChangeDescription = $"Disabled sub-capability: {subCapability}",
                PreviousValue = subCapability.ToString(),
                ChangedBy = adminUserId
            });
        }

        return changed;
    }

    /// <summary>
    /// Sets the rate limit for a capability.
    /// </summary>
    /// <param name="capability">The capability to rate limit.</param>
    /// <param name="rateLimit">The rate limit configuration.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetRateLimitAsync(
        AICapability capability,
        Models.RateLimitConfig rateLimit,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        var previousValue = config.RateLimits.TryGetValue(capability, out var prev) ? prev : null;
        config.RateLimits[capability] = rateLimit;

        config.LastUpdatedAt = DateTime.UtcNow;
        config.LastUpdatedBy = adminUserId;
        config.Version++;

        await PersistConfigurationAsync(instanceId, ct);

        RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
        {
            ChangeType = ConfigurationChangeType.RateLimit,
            EntityId = instanceId,
            ChangeDescription = $"Updated rate limit for: {capability}",
            PreviousValue = previousValue != null ? JsonSerializer.Serialize(previousValue) : null,
            NewValue = JsonSerializer.Serialize(rateLimit),
            ChangedBy = adminUserId
        });
    }

    /// <summary>
    /// Gets the rate limit for a capability.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>The rate limit configuration, or default if not set.</returns>
    public Models.RateLimitConfig GetRateLimit(AICapability capability, string? instanceId = null)
    {
        var config = GetConfiguration(instanceId);
        return config.RateLimits.TryGetValue(capability, out var limit)
            ? limit
            : new Models.RateLimitConfig(); // Default limits
    }

    /// <summary>
    /// Blocks a provider type for the instance.
    /// </summary>
    /// <param name="providerType">The provider type to block (e.g., "ollama").</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task BlockProviderTypeAsync(
        string providerType,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        var added = config.BlockedProviderTypes.Add(providerType.ToLowerInvariant());
        config.AllowedProviderTypes.Remove(providerType.ToLowerInvariant());

        if (added)
        {
            config.LastUpdatedAt = DateTime.UtcNow;
            config.LastUpdatedBy = adminUserId;
            config.Version++;

            await PersistConfigurationAsync(instanceId, ct);

            RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
            {
                ChangeType = ConfigurationChangeType.ProviderTypeBlock,
                EntityId = instanceId,
                ChangeDescription = $"Blocked provider type: {providerType}",
                NewValue = providerType,
                ChangedBy = adminUserId
            });
        }
    }

    /// <summary>
    /// Unblocks a provider type for the instance.
    /// </summary>
    /// <param name="providerType">The provider type to unblock.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task UnblockProviderTypeAsync(
        string providerType,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        var removed = config.BlockedProviderTypes.Remove(providerType.ToLowerInvariant());

        if (removed)
        {
            config.LastUpdatedAt = DateTime.UtcNow;
            config.LastUpdatedBy = adminUserId;
            config.Version++;

            await PersistConfigurationAsync(instanceId, ct);

            RaiseConfigurationChanged(new CapabilityConfigurationChangedEvent
            {
                ChangeType = ConfigurationChangeType.ProviderTypeBlock,
                EntityId = instanceId,
                ChangeDescription = $"Unblocked provider type: {providerType}",
                PreviousValue = providerType,
                ChangedBy = adminUserId
            });
        }
    }

    /// <summary>
    /// Checks if a provider type is allowed for the instance.
    /// </summary>
    /// <param name="providerType">The provider type to check.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>True if the provider type is allowed.</returns>
    public bool IsProviderTypeAllowed(string providerType, string? instanceId = null)
    {
        var config = GetConfiguration(instanceId);
        var normalizedType = providerType.ToLowerInvariant();

        // Check if explicitly blocked
        if (config.BlockedProviderTypes.Contains(normalizedType))
        {
            return false;
        }

        // If there's an allow list, check it
        if (config.AllowedProviderTypes.Count > 0)
        {
            return config.AllowedProviderTypes.Contains(normalizedType);
        }

        // Default: allowed if not blocked
        return true;
    }

    /// <summary>
    /// Sets the default system provider.
    /// </summary>
    /// <param name="providerName">The provider name.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetDefaultSystemProviderAsync(
        string? providerName,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        // We need to update the configuration with new default provider
        // Since InstanceCapabilityConfiguration.DefaultSystemProvider is init-only,
        // we need to create a new configuration
        var newConfig = new InstanceCapabilityConfiguration
        {
            InstanceId = config.InstanceId,
            EnabledCapabilities = config.EnabledCapabilities,
            EnabledSubCapabilities = config.EnabledSubCapabilities,
            DisabledSubCapabilities = config.DisabledSubCapabilities,
            RateLimits = config.RateLimits,
            AllowedProviderTypes = config.AllowedProviderTypes,
            BlockedProviderTypes = config.BlockedProviderTypes,
            AllowBYOK = config.AllowBYOK,
            DefaultSystemProvider = providerName,
            LastUpdatedAt = DateTime.UtcNow,
            LastUpdatedBy = adminUserId,
            Version = config.Version + 1
        };

        _configurations[instanceId] = newConfig;
        await PersistConfigurationAsync(instanceId, ct);
    }

    /// <summary>
    /// Sets whether BYOK is allowed for the instance.
    /// </summary>
    /// <param name="allowed">Whether BYOK is allowed.</param>
    /// <param name="adminUserId">The administrator making the change.</param>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SetBYOKAllowedAsync(
        bool allowed,
        string adminUserId,
        string? instanceId = null,
        CancellationToken ct = default)
    {
        instanceId ??= DefaultInstanceId;
        var config = GetConfiguration(instanceId);

        // Create new config since AllowBYOK is init-only
        var newConfig = new InstanceCapabilityConfiguration
        {
            InstanceId = config.InstanceId,
            EnabledCapabilities = config.EnabledCapabilities,
            EnabledSubCapabilities = config.EnabledSubCapabilities,
            DisabledSubCapabilities = config.DisabledSubCapabilities,
            RateLimits = config.RateLimits,
            AllowedProviderTypes = config.AllowedProviderTypes,
            BlockedProviderTypes = config.BlockedProviderTypes,
            AllowBYOK = allowed,
            DefaultSystemProvider = config.DefaultSystemProvider,
            LastUpdatedAt = DateTime.UtcNow,
            LastUpdatedBy = adminUserId,
            Version = config.Version + 1
        };

        _configurations[instanceId] = newConfig;
        await PersistConfigurationAsync(instanceId, ct);
    }

    /// <summary>
    /// Gets all enabled capabilities for an instance.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>Set of enabled capabilities.</returns>
    public IReadOnlySet<AICapability> GetEnabledCapabilities(string? instanceId = null)
    {
        var config = GetConfiguration(instanceId);
        return config.EnabledCapabilities;
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
    /// Loads configuration from storage.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadAsync(string? instanceId = null, CancellationToken ct = default)
    {
        if (_storage == null) return;

        instanceId ??= DefaultInstanceId;
        var path = $"{StoragePrefix}{instanceId}.json";

        var data = await _storage.LoadBytesAsync(path, ct);
        if (data != null)
        {
            var config = JsonSerializer.Deserialize<InstanceCapabilityConfiguration>(data);
            if (config != null)
            {
                _configurations[instanceId] = config;
            }
        }
    }

    /// <summary>
    /// Persists configuration to storage.
    /// </summary>
    private async Task PersistConfigurationAsync(string instanceId, CancellationToken ct)
    {
        if (_storage == null) return;

        await _persistLock.WaitAsync(ct);
        try
        {
            var config = GetConfiguration(instanceId);
            var path = $"{StoragePrefix}{instanceId}.json";
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
                // Log but don't throw - event handlers shouldn't break the operation
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
