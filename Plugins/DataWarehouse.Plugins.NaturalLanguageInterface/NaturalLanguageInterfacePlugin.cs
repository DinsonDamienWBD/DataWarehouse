// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Integrations;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using DataWarehouse.Plugins.NaturalLanguageInterface.Voice;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface;

/// <summary>
/// Natural Language Data Interaction Plugin.
/// Provides natural language interfaces for interacting with the DataWarehouse system.
///
/// Features:
/// - Natural Language Search: Query parsing, intent extraction, entity recognition
/// - Conversational Queries: Multi-turn dialogue with context management
/// - Voice Commands: Integration framework for Alexa, Google Assistant, Siri
/// - Query Explanation: Decision explanation with reasoning chains
/// - Data Storytelling: Narrative generation with visualizations
/// - Anomaly Explanation: Root cause analysis and impact assessment
///
/// Integrations:
/// - Slack Bot: Slash commands, interactive messages, event subscriptions
/// - Microsoft Teams: Bot Framework, adaptive cards, tab integration
/// - Discord Bot: Slash commands, rich embeds, guild management
/// - ChatGPT Plugin: ai-plugin.json manifest, OpenAPI schema
/// - Claude MCP Server: Model Context Protocol with tools and resources
/// - Generic LLM Adapter: For Copilot, Bard, Gemini, Llama, etc.
/// </summary>
public sealed class NaturalLanguageInterfacePlugin : FeaturePluginBase, IDisposable
{
    public override string Id => "datawarehouse.plugins.nl";
    public override string Name => "Natural Language Interface";
    public override string Version => "1.0.0";
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    // Core engines
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly ExplanationEngine _explanationEngine;
    private readonly StorytellingEngine _storytellingEngine;

    // Integrations
    private readonly ConcurrentDictionary<string, IIntegrationProvider> _integrations = new();
    private readonly ConcurrentDictionary<string, IVoiceHandler> _voiceHandlers = new();

    // AI Provider integration
    private readonly IAIProviderRegistry? _aiProviderRegistry;
    private readonly NLInterfaceConfig _config;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _shutdownCts = new();

    public NaturalLanguageInterfacePlugin(
        IAIProviderRegistry? aiProviderRegistry = null,
        NLInterfaceConfig? config = null,
        HttpClient? httpClient = null)
    {
        _aiProviderRegistry = aiProviderRegistry;
        _config = config ?? new NLInterfaceConfig();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        // Initialize engines with AI provider registry support
        _queryEngine = new QueryEngine(_aiProviderRegistry);
        _conversationEngine = new ConversationEngine(_queryEngine, _aiProviderRegistry);
        _explanationEngine = new ExplanationEngine(_aiProviderRegistry);
        _storytellingEngine = new StorytellingEngine(_aiProviderRegistry);
    }

    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            // Query capabilities
            new() { Name = "nl.query.search", DisplayName = "Search", Description = "Natural language search" },
            new() { Name = "nl.query.explain", DisplayName = "Explain", Description = "Explain query or decision" },
            new() { Name = "nl.query.translate", DisplayName = "Translate", Description = "Translate NL to command" },

            // Conversation capabilities
            new() { Name = "nl.conversation.start", DisplayName = "Start Conversation", Description = "Start new conversation" },
            new() { Name = "nl.conversation.message", DisplayName = "Message", Description = "Send message to conversation" },
            new() { Name = "nl.conversation.end", DisplayName = "End Conversation", Description = "End conversation" },

            // Voice capabilities
            new() { Name = "nl.voice.alexa", DisplayName = "Alexa", Description = "Handle Alexa request" },
            new() { Name = "nl.voice.google", DisplayName = "Google Assistant", Description = "Handle Google Assistant request" },
            new() { Name = "nl.voice.siri", DisplayName = "Siri", Description = "Handle Siri request" },

            // Integration capabilities
            new() { Name = "nl.integration.webhook", DisplayName = "Webhook", Description = "Handle integration webhook" },
            new() { Name = "nl.integration.configure", DisplayName = "Configure", Description = "Configure integration" },

            // Storytelling capabilities
            new() { Name = "nl.story.generate", DisplayName = "Generate Story", Description = "Generate data narrative" },
            new() { Name = "nl.story.report", DisplayName = "Generate Report", Description = "Generate report" },

            // Admin capabilities
            new() { Name = "nl.providers", DisplayName = "Providers", Description = "List AI providers" },
            new() { Name = "nl.integrations", DisplayName = "Integrations", Description = "List integrations" },
            new() { Name = "nl.stats", DisplayName = "Statistics", Description = "Get usage statistics" }
        ];
    }

    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "NaturalLanguageInterface";
        metadata["Description"] = "Natural language data interaction and conversational AI";
        metadata["SupportsStreaming"] = true;
        metadata["SupportsMultiTurnDialogue"] = true;
        metadata["SupportsVoice"] = true;
        metadata["ActiveIntegrations"] = _integrations.Keys.ToList();
        metadata["ActiveVoiceHandlers"] = _voiceHandlers.Keys.ToList();
        metadata["ProviderAgnostic"] = true;
        metadata["AIProviderConfigured"] = _aiProviderRegistry?.GetDefaultProvider() != null;
        return metadata;
    }

    public override async Task StartAsync(CancellationToken ct)
    {
        // Initialize integrations based on configuration
        await InitializeIntegrationsAsync(ct);

        // Initialize voice handlers
        InitializeVoiceHandlers();

        // Start conversation cleanup background task
        _ = Task.Run(() => ConversationCleanupLoopAsync(_shutdownCts.Token), _shutdownCts.Token);
    }

    public override async Task StopAsync()
    {
        // Signal shutdown
        _shutdownCts.Cancel();

        // Dispose integrations
        foreach (var integration in _integrations.Values.OfType<IDisposable>())
        {
            integration.Dispose();
        }
        _integrations.Clear();

        // Dispose voice handlers
        foreach (var handler in _voiceHandlers.Values.OfType<IDisposable>())
        {
            handler.Dispose();
        }
        _voiceHandlers.Clear();

        // Cleanup HTTP client
        _httpClient.Dispose();

        // Dispose cancellation token
        _shutdownCts.Dispose();

        await Task.CompletedTask;
    }

    public override async Task OnMessageAsync(PluginMessage message)
    {
        object? response = message.Type switch
        {
            // Query handlers
            "nl.query.search" => await HandleSearchAsync(message.Payload),
            "nl.query.explain" => await HandleExplainAsync(message.Payload),
            "nl.query.translate" => await HandleTranslateAsync(message.Payload),

            // Conversation handlers
            "nl.conversation.start" => HandleConversationStart(message.Payload),
            "nl.conversation.message" => await HandleConversationMessageAsync(message.Payload),
            "nl.conversation.end" => HandleConversationEnd(message.Payload),

            // Voice handlers
            "nl.voice.alexa" => await HandleVoiceRequestAsync("alexa", message.Payload),
            "nl.voice.google" => await HandleVoiceRequestAsync("google", message.Payload),
            "nl.voice.siri" => await HandleVoiceRequestAsync("siri", message.Payload),

            // Integration handlers
            "nl.integration.webhook" => await HandleIntegrationWebhookAsync(message.Payload),
            "nl.integration.configure" => HandleIntegrationConfigure(message.Payload),

            // Storytelling handlers
            "nl.story.generate" => await HandleStoryGenerateAsync(message.Payload),
            "nl.story.report" => await HandleReportGenerateAsync(message.Payload),

            // Admin handlers
            "nl.providers" => HandleListProviders(),
            "nl.integrations" => HandleListIntegrations(),
            "nl.stats" => HandleGetStats(),

            _ => new { error = $"Unknown command: {message.Type}" }
        };

        if (response != null && message.Payload != null)
        {
            message.Payload["_response"] = response;
        }
    }

    #region Query Handlers

    private async Task<object> HandleSearchAsync(Dictionary<string, object>? payload)
    {
        var query = GetString(payload, "query");

        if (string.IsNullOrEmpty(query))
        {
            return new { error = "query is required" };
        }

        try
        {
            var request = new NLSearchRequest
            {
                Query = query,
                UserId = GetString(payload, "userId"),
                ConversationId = GetString(payload, "conversationId"),
                MaxResults = GetInt(payload, "maxResults") ?? 50,
                LanguageHint = GetString(payload, "language"),
                Platform = GetString(payload, "platform") ?? "api",
                IncludeExplanation = true
            };

            var result = await _queryEngine.SearchAsync(request);

            return new
            {
                success = result.Success,
                response = result.Response,
                intent = result.Intent?.Type.ToString(),
                translation = result.Translation,
                totalCount = result.TotalCount,
                results = result.Results,
                suggestedFollowUps = result.SuggestedFollowUps,
                executionTimeMs = result.ExecutionTimeMs
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> HandleExplainAsync(Dictionary<string, object>? payload)
    {
        var itemId = GetString(payload, "itemId");
        var question = GetString(payload, "question");
        var explainType = GetString(payload, "type") ?? "tiering";

        try
        {
            ExplanationResult explanation;

            if (explainType == "tiering")
            {
                var context = new TieringDecisionContext
                {
                    FileName = itemId ?? "unknown",
                    TargetTier = GetString(payload, "targetTier") ?? "archive",
                    LastAccessDate = DateTime.UtcNow.AddDays(-90),
                    FileSize = GetLong(payload, "fileSize"),
                    AccessFrequency = GetInt(payload, "accessFrequency")
                };
                explanation = await _explanationEngine.ExplainTieringDecisionAsync(context);
            }
            else if (explainType == "spike")
            {
                // Use default context - StorageSpikeContext has its own field definitions
                var context = new StorageSpikeContext();
                explanation = await _explanationEngine.ExplainStorageSpikeAsync(context);
            }
            else if (explainType == "anomaly")
            {
                // Use default context - AnomalyContext has its own field definitions
                var context = new AnomalyContext();
                explanation = await _explanationEngine.ExplainAnomalyAsync(context);
            }
            else
            {
                // Use default context - QueryResultContext has its own field definitions
                var context = new QueryResultContext();
                explanation = await _explanationEngine.ExplainQueryResultAsync(context);
            }

            return new
            {
                success = explanation.Success,
                question = explanation.Question,
                summary = explanation.Summary,
                reasoningChain = explanation.ReasoningChain,
                timeline = explanation.Timeline,
                recommendations = explanation.Recommendations,
                confidence = explanation.Confidence
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> HandleTranslateAsync(Dictionary<string, object>? payload)
    {
        var query = GetString(payload, "query");

        if (string.IsNullOrEmpty(query))
        {
            return new { error = "query is required" };
        }

        try
        {
            var intent = await _queryEngine.ParseQueryAsync(query, null);
            var translation = _queryEngine.TranslateToCommand(intent);

            return new
            {
                success = translation.Success,
                intent = intent.Type.ToString(),
                entities = intent.Entities,
                command = translation.Command,
                parameters = translation.Parameters,
                explanation = translation.Explanation,
                confidence = translation.Confidence,
                warnings = translation.Warnings
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    #endregion

    #region Conversation Handlers

    private object HandleConversationStart(Dictionary<string, object>? payload)
    {
        var userId = GetString(payload, "userId") ?? $"user:{Guid.NewGuid():N}";
        var platform = GetString(payload, "platform") ?? "api";
        var channelId = GetString(payload, "channelId");

        var conversation = _conversationEngine.StartConversation(userId, platform, channelId);

        return new
        {
            success = true,
            conversationId = conversation.Id,
            userId = conversation.UserId,
            platform = conversation.Platform
        };
    }

    private async Task<object> HandleConversationMessageAsync(Dictionary<string, object>? payload)
    {
        var conversationId = GetString(payload, "conversationId");
        var message = GetString(payload, "message");

        if (string.IsNullOrEmpty(conversationId))
        {
            return new { error = "conversationId is required" };
        }

        if (string.IsNullOrEmpty(message))
        {
            return new { error = "message is required" };
        }

        try
        {
            // Get attachments if any
            List<MessageAttachment>? attachments = null;
            if (payload?.TryGetValue("attachments", out var attachObj) == true && attachObj is JsonElement elem)
            {
                attachments = JsonSerializer.Deserialize<List<MessageAttachment>>(elem.GetRawText());
            }

            var result = await _conversationEngine.ProcessMessageAsync(
                conversationId, message, attachments);

            return new
            {
                success = result.Success,
                response = result.Response,
                state = result.State.ToString(),
                data = result.Data,
                clarification = result.Clarification,
                suggestions = result.SuggestedFollowUps
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object HandleConversationEnd(Dictionary<string, object>? payload)
    {
        var conversationId = GetString(payload, "conversationId");

        if (string.IsNullOrEmpty(conversationId))
        {
            return new { error = "conversationId is required" };
        }

        _conversationEngine.EndConversation(conversationId);

        return new { success = true, conversationId };
    }

    #endregion

    #region Voice Handlers

    private async Task<object> HandleVoiceRequestAsync(string platform, Dictionary<string, object>? payload)
    {
        if (!_voiceHandlers.TryGetValue(platform, out var handler))
        {
            return new { error = $"Voice handler not configured: {platform}" };
        }

        try
        {
            var body = GetString(payload, "body") ?? "{}";
            var signature = GetString(payload, "signature");
            var headers = GetStringDictionary(payload, "headers") ?? new Dictionary<string, string>();

            // Validate signature
            if (!string.IsNullOrEmpty(signature) && !handler.ValidateSignature(body, signature, headers))
            {
                return new { error = "Invalid signature", statusCode = 401 };
            }

            // Parse request
            var request = JsonSerializer.Deserialize<VoiceRequest>(body) ?? new VoiceRequest();

            var response = await handler.HandleRequestAsync(request);

            return new
            {
                success = true,
                response = response,
                platform
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    #endregion

    #region Integration Handlers

    private async Task<object> HandleIntegrationWebhookAsync(Dictionary<string, object>? payload)
    {
        var platform = GetString(payload, "platform");

        if (string.IsNullOrEmpty(platform))
        {
            return new { error = "platform is required" };
        }

        if (!_integrations.TryGetValue(platform, out var integration))
        {
            return new { error = $"Integration not configured: {platform}" };
        }

        try
        {
            var body = GetString(payload, "body") ?? "{}";
            var signature = GetString(payload, "signature") ?? "";
            var headers = GetStringDictionary(payload, "headers") ?? new Dictionary<string, string>();
            var eventType = GetString(payload, "type") ?? "";

            // Validate webhook signature
            if (!integration.ValidateWebhookSignature(body, signature, headers))
            {
                return new { error = "Invalid webhook signature", statusCode = 401 };
            }

            // Parse webhook payload
            var webhookPayload = JsonSerializer.Deserialize<Dictionary<string, object>>(body)
                ?? new Dictionary<string, object>();

            var webhookEvent = new WebhookEvent
            {
                Type = eventType,
                Payload = webhookPayload,
                Headers = headers,
                Timestamp = DateTime.UtcNow
            };

            var response = await integration.HandleWebhookAsync(webhookEvent);

            return new
            {
                success = response.StatusCode >= 200 && response.StatusCode < 300,
                statusCode = response.StatusCode,
                body = response.Body,
                headers = response.Headers
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private object HandleIntegrationConfigure(Dictionary<string, object>? payload)
    {
        var platform = GetString(payload, "platform");

        if (string.IsNullOrEmpty(platform))
        {
            return new { error = "platform is required" };
        }

        if (!_integrations.TryGetValue(platform, out var integration))
        {
            return new { error = $"Integration not found: {platform}" };
        }

        var config = integration.GetConfiguration();

        return new
        {
            success = true,
            platform,
            isConfigured = integration.IsConfigured,
            webhookEndpoint = integration.GetWebhookEndpoint(),
            configuration = config
        };
    }

    #endregion

    #region Storytelling Handlers

    private async Task<object> HandleStoryGenerateAsync(Dictionary<string, object>? payload)
    {
        var topic = GetString(payload, "topic") ?? "storage";

        try
        {
            var request = new NarrativeRequest
            {
                TimeRange = new TimeRange
                {
                    Start = DateTimeOffset.FromUnixTimeSeconds(
                        GetLong(payload, "startDate") ?? DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds()).UtcDateTime,
                    End = DateTimeOffset.FromUnixTimeSeconds(
                        GetLong(payload, "endDate") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime
                }
            };

            var story = await _storytellingEngine.GenerateNarrativeAsync(request);

            return new
            {
                success = true,
                title = story.Title,
                sections = story.Sections,
                keyMetrics = story.KeyMetrics,
                trends = story.Trends,
                visualizationRecommendations = story.VisualizationRecommendations
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    private async Task<object> HandleReportGenerateAsync(Dictionary<string, object>? payload)
    {
        var reportType = GetString(payload, "type") ?? "storage";

        try
        {
            var timeRange = new TimeRange
            {
                Start = DateTimeOffset.FromUnixTimeSeconds(
                    GetLong(payload, "startDate") ?? DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds()).UtcDateTime,
                End = DateTimeOffset.FromUnixTimeSeconds(
                    GetLong(payload, "endDate") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).UtcDateTime
            };

            StoryResult report;

            if (reportType == "backup")
            {
                var request = new BackupHistoryRequest();
                report = await _storytellingEngine.SummarizeBackupHistoryAsync(request);
            }
            else
            {
                var request = new StorageReportRequest();
                report = await _storytellingEngine.GenerateStorageReportAsync(request);
            }

            return new
            {
                success = true,
                title = report.Title,
                sections = report.Sections,
                keyMetrics = report.KeyMetrics,
                trends = report.Trends,
                visualizationRecommendations = report.VisualizationRecommendations
            };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message };
        }
    }

    #endregion

    #region Admin Handlers

    private object HandleListProviders()
    {
        var providers = new List<object>();

        if (_aiProviderRegistry != null)
        {
            foreach (var provider in _aiProviderRegistry.GetAllProviders())
            {
                providers.Add(new
                {
                    id = provider.ProviderId,
                    name = provider.DisplayName,
                    isAvailable = provider.IsAvailable,
                    capabilities = provider.Capabilities.ToString()
                });
            }
        }

        return new
        {
            success = true,
            count = providers.Count,
            providers,
            defaultProvider = _aiProviderRegistry?.GetDefaultProvider()?.ProviderId
        };
    }

    private object HandleListIntegrations()
    {
        var integrations = _integrations.Select(i => new
        {
            platform = i.Key,
            name = i.Value.PlatformName,
            isConfigured = i.Value.IsConfigured,
            webhookEndpoint = i.Value.GetWebhookEndpoint()
        }).ToList();

        var voiceHandlers = _voiceHandlers.Select(v => new
        {
            platform = v.Key,
            name = v.Value.PlatformName,
            isConfigured = v.Value.IsConfigured
        }).ToList();

        return new
        {
            success = true,
            integrations,
            voiceHandlers
        };
    }

    private object HandleGetStats()
    {
        return new
        {
            success = true,
            integrations = _integrations.Count,
            voiceHandlers = _voiceHandlers.Count,
            aiProviderConfigured = _aiProviderRegistry?.GetDefaultProvider() != null
        };
    }

    #endregion

    #region Initialization

    private async Task InitializeIntegrationsAsync(CancellationToken ct)
    {
        // Initialize Slack integration
        if (_config.Slack?.Enabled == true)
        {
            var slack = new SlackIntegration(
                _queryEngine,
                _conversationEngine,
                _config.Slack,
                _httpClient);
            _integrations["slack"] = slack;
        }

        // Initialize Teams integration
        if (_config.Teams?.Enabled == true)
        {
            var teams = new TeamsIntegration(
                _queryEngine,
                _conversationEngine,
                _config.Teams,
                _httpClient);
            _integrations["teams"] = teams;
        }

        // Initialize Discord integration
        if (_config.Discord?.Enabled == true)
        {
            var discord = new DiscordIntegration(
                _queryEngine,
                _conversationEngine,
                _config.Discord,
                _httpClient);
            _integrations["discord"] = discord;

            // Register commands if configured
            if (_config.Discord.AutoRegisterCommands)
            {
                await discord.RegisterCommandsAsync(ct);
            }
        }

        // Initialize ChatGPT Plugin
        if (_config.ChatGPT?.Enabled == true)
        {
            var chatgpt = new ChatGPTPluginIntegration(
                _queryEngine,
                _conversationEngine,
                _config.ChatGPT);
            _integrations["chatgpt"] = chatgpt;
        }

        // Initialize Claude MCP Server
        if (_config.ClaudeMCP?.Enabled == true)
        {
            var mcp = new ClaudeMCPServer(
                _queryEngine,
                _conversationEngine,
                _explanationEngine,
                _storytellingEngine,
                _config.ClaudeMCP);
            _integrations["claude_mcp"] = mcp;
        }

        // Initialize Generic LLM adapters
        foreach (var llmConfig in _config.GenericLLMs ?? Enumerable.Empty<GenericLLMConfig>())
        {
            var adapter = new GenericLLMAdapter(
                _queryEngine,
                _conversationEngine,
                llmConfig);
            _integrations[llmConfig.PlatformId] = adapter;
        }
    }

    private void InitializeVoiceHandlers()
    {
        // Initialize Alexa handler
        if (_config.Alexa?.Enabled == true)
        {
            var alexa = new AlexaHandler(
                _queryEngine,
                _conversationEngine,
                _config.Alexa);
            _voiceHandlers["alexa"] = alexa;
        }

        // Initialize Google Assistant handler
        if (_config.GoogleAssistant?.Enabled == true)
        {
            var google = new GoogleAssistantHandler(
                _queryEngine,
                _conversationEngine,
                _config.GoogleAssistant);
            _voiceHandlers["google"] = google;
        }

        // Initialize Siri handler
        if (_config.Siri?.Enabled == true)
        {
            var siri = new SiriHandler(
                _queryEngine,
                _conversationEngine,
                _config.Siri);
            _voiceHandlers["siri"] = siri;
        }
    }

    private async Task ConversationCleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                // Cleanup is handled internally by ConversationEngine timer
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #endregion

    #region Helpers

    private static string? GetString(Dictionary<string, object>? payload, string key)
    {
        if (payload?.TryGetValue(key, out var val) != true) return null;
        return val switch
        {
            string s => s,
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString(),
            _ => val?.ToString()
        };
    }

    private static int? GetInt(Dictionary<string, object>? payload, string key)
    {
        if (payload?.TryGetValue(key, out var val) != true) return null;
        return val switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetInt32(),
            _ => null
        };
    }

    private static long? GetLong(Dictionary<string, object>? payload, string key)
    {
        if (payload?.TryGetValue(key, out var val) != true) return null;
        return val switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            JsonElement e when e.ValueKind == JsonValueKind.Number => e.GetInt64(),
            _ => null
        };
    }

    private static Dictionary<string, string>? GetStringDictionary(Dictionary<string, object>? payload, string key)
    {
        if (payload?.TryGetValue(key, out var val) != true || val == null) return null;
        if (val is Dictionary<string, string> dict) return dict;
        if (val is JsonElement e && e.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, string>();
            foreach (var prop in e.EnumerateObject())
            {
                result[prop.Name] = prop.Value.GetString() ?? "";
            }
            return result;
        }
        return null;
    }

    #endregion

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();

        foreach (var integration in _integrations.Values.OfType<IDisposable>())
        {
            integration.Dispose();
        }

        foreach (var handler in _voiceHandlers.Values.OfType<IDisposable>())
        {
            handler.Dispose();
        }

        _httpClient.Dispose();
    }
}

/// <summary>
/// Configuration for the Natural Language Interface plugin.
/// </summary>
public sealed class NLInterfaceConfig
{
    /// <summary>Slack integration configuration.</summary>
    public Integrations.SlackConfig? Slack { get; init; }

    /// <summary>Microsoft Teams integration configuration.</summary>
    public Integrations.TeamsConfig? Teams { get; init; }

    /// <summary>Discord integration configuration.</summary>
    public Integrations.DiscordConfig? Discord { get; init; }

    /// <summary>ChatGPT Plugin configuration.</summary>
    public Integrations.ChatGPTPluginConfig? ChatGPT { get; init; }

    /// <summary>Claude MCP Server configuration.</summary>
    public Integrations.ClaudeMCPConfig? ClaudeMCP { get; init; }

    /// <summary>Generic LLM adapter configurations.</summary>
    public List<Integrations.GenericLLMConfig>? GenericLLMs { get; init; }

    /// <summary>Alexa integration configuration.</summary>
    public Voice.AlexaConfig? Alexa { get; init; }

    /// <summary>Google Assistant integration configuration.</summary>
    public Voice.GoogleAssistantConfig? GoogleAssistant { get; init; }

    /// <summary>Siri integration configuration.</summary>
    public Voice.SiriConfig? Siri { get; init; }

    /// <summary>Session timeout for conversations.</summary>
    public TimeSpan SessionTimeout { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Maximum concurrent conversations.</summary>
    public int MaxConcurrentConversations { get; init; } = 10000;
}
