// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using DataWarehouse.Plugins.AIInterface.Registry;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.AI;
using System.Text.Json;

namespace DataWarehouse.Plugins.AIInterface;

/// <summary>
/// Unified AI Interface plugin that provides a single, channel-agnostic entry point for all AI integrations.
/// </summary>
/// <remarks>
/// <para>
/// This plugin manages all integration channels including:
/// <list type="bullet">
/// <item><b>Chat Platforms:</b> Slack, Microsoft Teams, Discord</item>
/// <item><b>Voice Assistants:</b> Amazon Alexa, Google Assistant, Apple Siri</item>
/// <item><b>LLM Platforms:</b> ChatGPT Plugin, Claude MCP, Generic Webhooks</item>
/// </list>
/// </para>
/// <para>
/// <b>Key Design Principle:</b> This plugin performs NO AI work itself. It only:
/// <list type="number">
/// <item>Receives requests from external channels</item>
/// <item>Translates them to Intelligence requests</item>
/// <item>Routes to UltimateIntelligence plugin via message bus: <c>messageBus.RequestAsync("intelligence.*", payload)</c></item>
/// <item>Returns responses formatted for the source channel</item>
/// </list>
/// </para>
/// <para>
/// This separation of concerns ensures:
/// <list type="bullet">
/// <item>All AI logic is centralized in the UltimateIntelligence plugin</item>
/// <item>Channels can be enabled/disabled without affecting Intelligence capabilities</item>
/// <item>New channels can be added without modifying Intelligence logic</item>
/// <item>Consistent Intelligence behavior across all integration points</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Enable/disable channels at runtime
/// plugin.EnableChannel("slack");
/// plugin.DisableChannel("discord");
///
/// // Configure a channel
/// plugin.ConfigureChannel("slack", new ChannelConfiguration
/// {
///     Enabled = true,
///     ApiKey = "xoxb-...",
///     ApiSecret = "signing-secret..."
/// });
///
/// // Handle an incoming request
/// var response = await plugin.HandleRequestAsync("slack", new ChannelRequest
/// {
///     Type = "slash_command",
///     Payload = new Dictionary&lt;string, object&gt; { ["command"] = "/dw", ["text"] = "search for documents" }
/// });
/// </code>
/// </example>
public sealed class AIInterfacePlugin : PluginBase
{
    private readonly ChannelRegistry _registry;
    private readonly AIInterfaceConfig _config;
    private readonly Channels.IMessageBus? _messageBus;
    private bool _isInitialized;

    /// <summary>
    /// Gets the channel registry for managing integration channels.
    /// </summary>
    public ChannelRegistry Registry => _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="AIInterfacePlugin"/> class.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="messageBus">Message bus for routing Intelligence requests.</param>
    public AIInterfacePlugin(AIInterfaceConfig? config = null, Channels.IMessageBus? messageBus = null)
    {
        _config = config ?? new AIInterfaceConfig();
        _messageBus = messageBus;
        _registry = new ChannelRegistry(messageBus);
    }

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.plugins.aiinterface";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InterfaceProvider;

    /// <inheritdoc />
    public override string Name => "AIInterface";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <summary>
    /// Initializes the plugin with channel registration.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Register all channels
        RegisterChannels();

        // Apply initial configuration
        ApplyConfiguration();

        // Register plugin knowledge with Intelligence
        RegisterPluginKnowledge();

        _isInitialized = true;

        await Task.CompletedTask;
    }

    /// <summary>
    /// Registers the plugin's capabilities and channel status as knowledge with the Intelligence plugin.
    /// </summary>
    private void RegisterPluginKnowledge()
    {
        if (_messageBus == null) return;

        try
        {
            var channelSummaries = _registry.GetChannelSummaries();
            var pluginKnowledge = KnowledgeObject.CreateCapabilityKnowledge(
                pluginId: Id,
                pluginName: Name,
                operations: new[]
                {
                    "manage.integration.channels",
                    "route.external.requests",
                    "validate.webhooks",
                    "format.channel.responses"
                },
                constraints: new Dictionary<string, object>
                {
                    ["totalChannels"] = channelSummaries.Count(),
                    ["enabledChannels"] = channelSummaries.Count(c => c.IsEnabled),
                    ["categories"] = channelSummaries.Select(c => c.Category.ToString()).Distinct().ToArray()
                }
            );

            // Publish knowledge registration
            _messageBus.Publish("intelligence.knowledge.register", new Dictionary<string, object>
            {
                ["knowledge"] = pluginKnowledge
            });
        }
        catch
        {
            // Silent failure - knowledge registration is optional
        }
    }

    /// <summary>
    /// Shuts down the plugin and disposes resources.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        // Dispose any channels that implement IDisposable
        foreach (var channel in _registry.GetAllChannels())
        {
            if (channel is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _isInitialized = false;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Handles an incoming request for a specific channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <param name="request">The incoming request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The channel response.</returns>
    /// <exception cref="InvalidOperationException">Plugin not initialized.</exception>
    /// <exception cref="ArgumentException">Channel not found or disabled.</exception>
    public async Task<ChannelResponse> HandleRequestAsync(string channelId, ChannelRequest request, CancellationToken ct = default)
    {
        EnsureInitialized();

        var channel = _registry.GetChannel(channelId);
        if (channel == null)
        {
            return ChannelResponse.Error(404, $"Channel not found: {channelId}");
        }

        if (!channel.IsEnabled)
        {
            return ChannelResponse.Error(403, $"Channel is disabled: {channelId}");
        }

        try
        {
            var response = await channel.HandleRequestAsync(request, ct);
            return response;
        }
        catch (Exception ex)
        {
            return ChannelResponse.Error(500, $"Internal error: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates an incoming webhook signature.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <param name="body">The raw request body.</param>
    /// <param name="signature">The signature value.</param>
    /// <param name="headers">The request headers.</param>
    /// <returns>True if the signature is valid.</returns>
    public bool ValidateSignature(string channelId, string body, string signature, IDictionary<string, string> headers)
    {
        var channel = _registry.GetChannel(channelId);
        return channel?.ValidateSignature(body, signature, headers) ?? false;
    }

    /// <summary>
    /// Enables a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>True if the channel was enabled successfully.</returns>
    public bool EnableChannel(string channelId)
    {
        return _registry.EnableChannel(channelId);
    }

    /// <summary>
    /// Disables a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>True if the channel was disabled successfully.</returns>
    public bool DisableChannel(string channelId)
    {
        return _registry.DisableChannel(channelId);
    }

    /// <summary>
    /// Configures a channel with the specified settings.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <param name="configuration">The configuration to apply.</param>
    public void ConfigureChannel(string channelId, ChannelConfiguration configuration)
    {
        _registry.SetConfiguration(channelId, configuration);
    }

    /// <summary>
    /// Gets a channel by identifier.
    /// </summary>
    /// <typeparam name="T">The expected channel type.</typeparam>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>The channel, or null if not found.</returns>
    public T? GetChannel<T>(string channelId) where T : class, IIntegrationChannel
    {
        return _registry.GetChannel<T>(channelId);
    }

    /// <summary>
    /// Gets summaries of all registered channels.
    /// </summary>
    /// <returns>Channel summaries.</returns>
    public IEnumerable<ChannelSummary> GetChannelSummaries()
    {
        return _registry.GetChannelSummaries();
    }

    /// <summary>
    /// Gets the health status of all channels.
    /// </summary>
    /// <returns>Registry health information.</returns>
    public RegistryHealth GetHealth()
    {
        return _registry.GetHealth();
    }

    /// <summary>
    /// Gets the webhook endpoint URL for a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>The webhook endpoint URL, or null if channel not found.</returns>
    public string? GetWebhookEndpoint(string channelId)
    {
        return _registry.GetChannel(channelId)?.GetWebhookEndpoint();
    }

    /// <summary>
    /// Gets the configuration/manifest for a channel.
    /// </summary>
    /// <param name="channelId">The channel identifier.</param>
    /// <returns>The configuration object, or null if channel not found.</returns>
    public object? GetChannelConfiguration(string channelId)
    {
        return _registry.GetChannel(channelId)?.GetConfiguration();
    }

    #region Private Methods

    private void RegisterChannels()
    {
        // Chat platforms
        if (_config.EnableSlack)
        {
            _registry.RegisterChannel(new SlackChannel(_config.Slack));
        }

        if (_config.EnableTeams)
        {
            _registry.RegisterChannel(new TeamsChannel(_config.Teams));
        }

        if (_config.EnableDiscord)
        {
            _registry.RegisterChannel(new DiscordChannel(_config.Discord));
        }

        // Voice assistants
        if (_config.EnableAlexa)
        {
            _registry.RegisterChannel(new AlexaChannel(_config.Alexa));
        }

        if (_config.EnableGoogleAssistant)
        {
            _registry.RegisterChannel(new GoogleAssistantChannel(_config.GoogleAssistant));
        }

        if (_config.EnableSiri)
        {
            _registry.RegisterChannel(new SiriChannel(_config.Siri));
        }

        // LLM platforms
        if (_config.EnableChatGPT)
        {
            _registry.RegisterChannel(new ChatGPTPluginChannel(_config.ChatGPT));
        }

        if (_config.EnableClaudeMCP)
        {
            _registry.RegisterChannel(new ClaudeMCPChannel(_config.ClaudeMCP));
        }

        if (_config.EnableGenericWebhook)
        {
            _registry.RegisterChannel(new GenericWebhookChannel(_config.GenericWebhook));
        }
    }

    private void ApplyConfiguration()
    {
        // Apply any channel-specific configurations from the main config
        foreach (var kvp in _config.ChannelConfigurations)
        {
            _registry.SetConfiguration(kvp.Key, kvp.Value);
        }
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Plugin not initialized. Call InitializeAsync first.");
        }
    }

    #endregion
}

/// <summary>
/// Configuration for the AI Interface plugin.
/// </summary>
public sealed class AIInterfaceConfig
{
    #region Channel Enablement

    /// <summary>Whether to enable Slack integration.</summary>
    public bool EnableSlack { get; init; } = true;

    /// <summary>Whether to enable Microsoft Teams integration.</summary>
    public bool EnableTeams { get; init; } = true;

    /// <summary>Whether to enable Discord integration.</summary>
    public bool EnableDiscord { get; init; } = true;

    /// <summary>Whether to enable Amazon Alexa integration.</summary>
    public bool EnableAlexa { get; init; } = true;

    /// <summary>Whether to enable Google Assistant integration.</summary>
    public bool EnableGoogleAssistant { get; init; } = true;

    /// <summary>Whether to enable Apple Siri integration.</summary>
    public bool EnableSiri { get; init; } = true;

    /// <summary>Whether to enable ChatGPT Plugin integration.</summary>
    public bool EnableChatGPT { get; init; } = true;

    /// <summary>Whether to enable Claude MCP integration.</summary>
    public bool EnableClaudeMCP { get; init; } = true;

    /// <summary>Whether to enable Generic Webhook integration.</summary>
    public bool EnableGenericWebhook { get; init; } = true;

    #endregion

    #region Channel Configurations

    /// <summary>Slack channel configuration.</summary>
    public SlackChannelConfig? Slack { get; init; }

    /// <summary>Teams channel configuration.</summary>
    public TeamsChannelConfig? Teams { get; init; }

    /// <summary>Discord channel configuration.</summary>
    public DiscordChannelConfig? Discord { get; init; }

    /// <summary>Alexa channel configuration.</summary>
    public AlexaChannelConfig? Alexa { get; init; }

    /// <summary>Google Assistant channel configuration.</summary>
    public GoogleAssistantChannelConfig? GoogleAssistant { get; init; }

    /// <summary>Siri channel configuration.</summary>
    public SiriChannelConfig? Siri { get; init; }

    /// <summary>ChatGPT Plugin channel configuration.</summary>
    public ChatGPTChannelConfig? ChatGPT { get; init; }

    /// <summary>Claude MCP channel configuration.</summary>
    public ClaudeMCPChannelConfig? ClaudeMCP { get; init; }

    /// <summary>Generic Webhook channel configuration.</summary>
    public GenericWebhookChannelConfig? GenericWebhook { get; init; }

    /// <summary>Additional per-channel configurations.</summary>
    public Dictionary<string, ChannelConfiguration> ChannelConfigurations { get; init; } = new();

    #endregion
}

