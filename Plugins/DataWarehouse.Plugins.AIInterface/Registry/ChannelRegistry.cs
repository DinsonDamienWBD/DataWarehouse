// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIInterface.Registry;

/// <summary>
/// Registry for managing integration channels.
/// Provides enable/disable, configuration, and discovery functionality for all channels.
/// </summary>
/// <remarks>
/// <para>
/// The channel registry maintains the state of all integration channels and provides:
/// <list type="bullet">
/// <item>Dynamic enable/disable of individual channels</item>
/// <item>Per-channel configuration storage</item>
/// <item>Channel discovery and enumeration</item>
/// <item>Health monitoring across all channels</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ChannelRegistry
{
    private readonly ConcurrentDictionary<string, IIntegrationChannel> _channels = new();
    private readonly ConcurrentDictionary<string, ChannelConfiguration> _configurations = new();
    private readonly ConcurrentDictionary<string, bool> _enabledState = new();
    private readonly IMessageBus? _messageBus;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRegistry"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for routing AI requests.</param>
    public ChannelRegistry(IMessageBus? messageBus = null)
    {
        _messageBus = messageBus;
    }

    /// <summary>
    /// Gets all available channel identifiers.
    /// </summary>
    public static IReadOnlyList<string> AvailableChannelIds => new[]
    {
        // Chat platforms
        "slack",
        "teams",
        "discord",

        // Voice assistants
        "alexa",
        "google_assistant",
        "siri",

        // LLM platforms
        "chatgpt",
        "claude_mcp",
        "generic_webhook"
    };

    /// <summary>
    /// Registers a channel with the registry.
    /// </summary>
    /// <param name="channel">The channel to register.</param>
    public void RegisterChannel(IIntegrationChannel channel)
    {
        if (channel is IntegrationChannelBase baseChannel)
        {
            baseChannel.Initialize(_messageBus);
        }

        _channels[channel.ChannelId] = channel;

        // Restore enabled state if previously set
        if (_enabledState.TryGetValue(channel.ChannelId, out var enabled))
        {
            channel.SetEnabled(enabled);
        }

        // Apply any stored configuration
        if (_configurations.TryGetValue(channel.ChannelId, out var config))
        {
            ApplyConfiguration(channel, config);
        }
    }

    /// <summary>
    /// Gets a channel by its identifier.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>The channel, or null if not found.</returns>
    public IIntegrationChannel? GetChannel(string channelId)
    {
        return _channels.TryGetValue(channelId, out var channel) ? channel : null;
    }

    /// <summary>
    /// Gets a channel cast to a specific type.
    /// </summary>
    /// <typeparam name="T">The channel type.</typeparam>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>The channel, or null if not found or wrong type.</returns>
    public T? GetChannel<T>(string channelId) where T : class, IIntegrationChannel
    {
        return GetChannel(channelId) as T;
    }

    /// <summary>
    /// Gets all registered channels.
    /// </summary>
    /// <returns>All registered channels.</returns>
    public IEnumerable<IIntegrationChannel> GetAllChannels()
    {
        return _channels.Values;
    }

    /// <summary>
    /// Gets all enabled channels.
    /// </summary>
    /// <returns>All enabled channels.</returns>
    public IEnumerable<IIntegrationChannel> GetEnabledChannels()
    {
        return _channels.Values.Where(c => c.IsEnabled);
    }

    /// <summary>
    /// Gets channels by category.
    /// </summary>
    /// <param name="category">The channel category.</param>
    /// <returns>Channels in the specified category.</returns>
    public IEnumerable<IIntegrationChannel> GetChannelsByCategory(ChannelCategory category)
    {
        return _channels.Values.Where(c => c.Category == category);
    }

    /// <summary>
    /// Enables a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>True if the channel was enabled successfully.</returns>
    public bool EnableChannel(string channelId)
    {
        _enabledState[channelId] = true;

        if (_channels.TryGetValue(channelId, out var channel))
        {
            channel.SetEnabled(true);
            return channel.IsEnabled;
        }

        return false;
    }

    /// <summary>
    /// Disables a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>True if the channel was disabled successfully.</returns>
    public bool DisableChannel(string channelId)
    {
        _enabledState[channelId] = false;

        if (_channels.TryGetValue(channelId, out var channel))
        {
            channel.SetEnabled(false);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a channel is enabled.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>True if the channel is enabled.</returns>
    public bool IsChannelEnabled(string channelId)
    {
        if (_channels.TryGetValue(channelId, out var channel))
        {
            return channel.IsEnabled;
        }

        return _enabledState.TryGetValue(channelId, out var enabled) && enabled;
    }

    /// <summary>
    /// Sets the configuration for a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <param name="configuration">The configuration to set.</param>
    public void SetConfiguration(string channelId, ChannelConfiguration configuration)
    {
        _configurations[channelId] = configuration;

        if (_channels.TryGetValue(channelId, out var channel))
        {
            ApplyConfiguration(channel, configuration);
        }
    }

    /// <summary>
    /// Gets the configuration for a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>The channel configuration, or null if not set.</returns>
    public ChannelConfiguration? GetConfiguration(string channelId)
    {
        return _configurations.TryGetValue(channelId, out var config) ? config : null;
    }

    /// <summary>
    /// Gets the health status of all channels.
    /// </summary>
    /// <returns>Health status for all channels.</returns>
    public RegistryHealth GetHealth()
    {
        var channelHealths = _channels.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.GetHealth());

        return new RegistryHealth
        {
            TotalChannels = _channels.Count,
            EnabledChannels = _channels.Values.Count(c => c.IsEnabled),
            ConfiguredChannels = _channels.Values.Count(c => c.IsConfigured),
            HealthyChannels = channelHealths.Values.Count(h => h.IsHealthy),
            ChannelHealth = channelHealths
        };
    }

    /// <summary>
    /// Gets a summary of all channels for display.
    /// </summary>
    /// <returns>Summary information for all channels.</returns>
    public IEnumerable<ChannelSummary> GetChannelSummaries()
    {
        return _channels.Values.Select(c => new ChannelSummary
        {
            ChannelId = c.ChannelId,
            ChannelName = c.ChannelName,
            Category = c.Category,
            IsConfigured = c.IsConfigured,
            IsEnabled = c.IsEnabled,
            WebhookEndpoint = c.GetWebhookEndpoint(),
            Health = c.GetHealth()
        });
    }

    /// <summary>
    /// Applies configuration to a channel.
    /// </summary>
    private void ApplyConfiguration(IIntegrationChannel channel, ChannelConfiguration config)
    {
        // Apply enabled state from config
        if (config.Enabled.HasValue)
        {
            channel.SetEnabled(config.Enabled.Value);
        }

        // Additional configuration would be applied through reflection or specific interfaces
        // This is a hook point for channel-specific configuration
    }
}

/// <summary>
/// Configuration for an integration channel.
/// </summary>
public sealed class ChannelConfiguration
{
    /// <summary>Gets or sets whether the channel is enabled.</summary>
    public bool? Enabled { get; init; }

    /// <summary>Gets or sets the API key or token.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Gets or sets the API secret or signing key.</summary>
    public string? ApiSecret { get; init; }

    /// <summary>Gets or sets the webhook URL base.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Gets or sets the application/bot identifier.</summary>
    public string? AppId { get; init; }

    /// <summary>Gets or sets additional configuration settings.</summary>
    public Dictionary<string, object> Settings { get; init; } = new();

    /// <summary>
    /// Gets a setting value.
    /// </summary>
    /// <typeparam name="T">The expected type.</typeparam>
    /// <param name="key">The setting key.</param>
    /// <param name="defaultValue">Default value if not found.</param>
    /// <returns>The setting value.</returns>
    public T? GetSetting<T>(string key, T? defaultValue = default)
    {
        if (!Settings.TryGetValue(key, out var value))
            return defaultValue;

        if (value is T typed)
            return typed;

        if (value is JsonElement element)
        {
            try
            {
                return element.Deserialize<T>();
            }
            catch
            {
                return defaultValue;
            }
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }
}

/// <summary>
/// Health status of the channel registry.
/// </summary>
public sealed class RegistryHealth
{
    /// <summary>Gets or sets the total number of registered channels.</summary>
    public int TotalChannels { get; init; }

    /// <summary>Gets or sets the number of enabled channels.</summary>
    public int EnabledChannels { get; init; }

    /// <summary>Gets or sets the number of configured channels.</summary>
    public int ConfiguredChannels { get; init; }

    /// <summary>Gets or sets the number of healthy channels.</summary>
    public int HealthyChannels { get; init; }

    /// <summary>Gets or sets the health status of each channel.</summary>
    public Dictionary<string, ChannelHealth> ChannelHealth { get; init; } = new();
}

/// <summary>
/// Summary information for a channel.
/// </summary>
public sealed class ChannelSummary
{
    /// <summary>Gets or sets the channel identifier.</summary>
    public string ChannelId { get; init; } = string.Empty;

    /// <summary>Gets or sets the channel name.</summary>
    public string ChannelName { get; init; } = string.Empty;

    /// <summary>Gets or sets the channel category.</summary>
    public ChannelCategory Category { get; init; }

    /// <summary>Gets or sets whether the channel is configured.</summary>
    public bool IsConfigured { get; init; }

    /// <summary>Gets or sets whether the channel is enabled.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Gets or sets the webhook endpoint.</summary>
    public string WebhookEndpoint { get; init; } = string.Empty;

    /// <summary>Gets or sets the channel health.</summary>
    public ChannelHealth Health { get; init; } = new();
}
