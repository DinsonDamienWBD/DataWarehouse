// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AIInterface.Channels;

/// <summary>
/// Slack integration channel.
/// Routes Slack requests (slash commands, interactive messages, events) to UltimateIntelligence via message bus.
/// </summary>
/// <remarks>
/// <para>
/// This channel handles:
/// <list type="bullet">
/// <item>Slash commands (/dw, /dw-search, /dw-status, /dw-backup)</item>
/// <item>Interactive messages and block actions</item>
/// <item>Event subscriptions (app_mention, message.im)</item>
/// <item>View submissions from modals</item>
/// </list>
/// </para>
/// <para>
/// All AI processing is delegated to the UltimateIntelligence plugin. This channel only handles
/// Slack-specific protocol translation and formatting.
/// </para>
/// </remarks>
public sealed class SlackChannel : IntegrationChannelBase, IDisposable
{
    private readonly SlackChannelConfig _config;
    private readonly HttpClient _httpClient;

    /// <inheritdoc />
    public override string ChannelId => "slack";

    /// <inheritdoc />
    public override string ChannelName => "Slack";

    /// <inheritdoc />
    public override ChannelCategory Category => ChannelCategory.Chat;

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.SigningSecret) &&
                                         !string.IsNullOrEmpty(_config.BotToken);

    /// <summary>
    /// Initializes a new instance of the <see cref="SlackChannel"/> class.
    /// </summary>
    /// <param name="config">Slack configuration.</param>
    /// <param name="httpClient">HTTP client for API calls.</param>
    public SlackChannel(SlackChannelConfig? config = null, HttpClient? httpClient = null)
    {
        _config = config ?? new SlackChannelConfig();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        if (!string.IsNullOrEmpty(_config.BotToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.BotToken);
        }
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        return request.Type switch
        {
            "url_verification" => HandleUrlVerification(request),
            "event_callback" => await HandleEventCallbackAsync(request, ct),
            "slash_command" => await HandleSlashCommandAsync(request, ct),
            "interactive_message" or "block_actions" => await HandleInteractiveMessageAsync(request, ct),
            "view_submission" => await HandleViewSubmissionAsync(request, ct),
            _ => ChannelResponse.Success(new { ok = true })
        };
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        if (string.IsNullOrEmpty(_config.SigningSecret))
            return false;

        if (!headers.TryGetValue("X-Slack-Signature", out var slackSignature) ||
            !headers.TryGetValue("X-Slack-Request-Timestamp", out var timestamp))
        {
            return false;
        }

        // Check timestamp to prevent replay attacks
        if (!long.TryParse(timestamp, out var ts))
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > 300) // 5 minute tolerance
            return false;

        // Compute expected signature
        var sigBasestring = $"v0:{timestamp}:{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.SigningSecret));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(sigBasestring));
        var expectedSignature = "v0=" + Convert.ToHexString(hashBytes).ToLower();

        return slackSignature == expectedSignature;
    }

    /// <inheritdoc />
    public override string GetWebhookEndpoint() => $"{_config.BaseUrl}/api/ai/channels/slack/webhook";

    /// <inheritdoc />
    public override object GetConfiguration()
    {
        return new
        {
            app = new
            {
                display_name = "DataWarehouse AI",
                description = "AI-powered data warehouse management",
                background_color = "#0066cc"
            },
            features = new
            {
                bot_user = new
                {
                    display_name = "DataWarehouse AI",
                    always_online = true
                },
                slash_commands = new object[]
                {
                    new { command = "/dw", url = $"{_config.BaseUrl}/api/ai/channels/slack/slash", description = "Query your DataWarehouse with AI", usage_hint = "[natural language query]" },
                    new { command = "/dw-search", url = $"{_config.BaseUrl}/api/ai/channels/slack/slash", description = "AI-powered file search", usage_hint = "[search query]" },
                    new { command = "/dw-status", url = $"{_config.BaseUrl}/api/ai/channels/slack/slash", description = "Get AI storage insights" },
                    new { command = "/dw-backup", url = $"{_config.BaseUrl}/api/ai/channels/slack/slash", description = "AI backup management", usage_hint = "[create|list|verify]" }
                },
                interactivity = new
                {
                    is_enabled = true,
                    request_url = $"{_config.BaseUrl}/api/ai/channels/slack/interactive"
                }
            },
            oauth_config = new
            {
                redirect_urls = new[] { $"{_config.BaseUrl}/api/ai/channels/slack/oauth/callback" },
                scopes = new
                {
                    bot = new[] { "app_mentions:read", "channels:history", "chat:write", "commands", "im:history", "im:read", "im:write", "users:read" }
                }
            },
            settings = new
            {
                event_subscriptions = new
                {
                    request_url = $"{_config.BaseUrl}/api/ai/channels/slack/events",
                    bot_events = new[] { "app_mention", "message.im" }
                }
            }
        };
    }

    #region Request Handlers

    private ChannelResponse HandleUrlVerification(ChannelRequest request)
    {
        if (request.Payload.TryGetValue("challenge", out var challenge))
        {
            return ChannelResponse.Challenge(challenge?.ToString() ?? "");
        }
        return ChannelResponse.Error(400, "Missing challenge");
    }

    private async Task<ChannelResponse> HandleEventCallbackAsync(ChannelRequest request, CancellationToken ct)
    {
        if (!request.Payload.TryGetValue("event", out var eventObj))
        {
            return ChannelResponse.Success();
        }

        var eventJson = JsonSerializer.Serialize(eventObj);
        var slackEvent = JsonSerializer.Deserialize<SlackEvent>(eventJson);

        if (slackEvent == null || !string.IsNullOrEmpty(slackEvent.BotId))
        {
            // Ignore bot messages to prevent loops
            return ChannelResponse.Success();
        }

        // Extract team ID for user identification
        var teamId = request.Payload.TryGetValue("team_id", out var tid) ? tid?.ToString() : "unknown";
        var userId = $"slack:{teamId}:{slackEvent.User}";
        var conversationId = $"slack:{slackEvent.Channel}:{slackEvent.ThreadTimestamp ?? slackEvent.Timestamp}";

        // Clean the message text
        var text = slackEvent.Text;
        if (!string.IsNullOrEmpty(_config.BotUserId))
        {
            text = text.Replace($"<@{_config.BotUserId}>", "").Trim();
        }

        // Route to AI via message bus
        var aiResponse = await RouteToIntelligenceAsync(
            "intelligence.request.conversation",
            new Dictionary<string, object>
            {
                ["message"] = text,
                ["platform"] = "slack"
            },
            userId,
            conversationId,
            ct);

        // Send response to Slack
        await SendSlackMessageAsync(slackEvent.Channel, aiResponse, slackEvent.ThreadTimestamp ?? slackEvent.Timestamp, ct);

        return ChannelResponse.Success();
    }

    private async Task<ChannelResponse> HandleSlashCommandAsync(ChannelRequest request, CancellationToken ct)
    {
        var command = GetPayloadValue<string>(request.Payload, "command") ?? "/dw";
        var text = GetPayloadValue<string>(request.Payload, "text") ?? "";
        var channelId = GetPayloadValue<string>(request.Payload, "channel_id") ?? "";
        var userId = GetPayloadValue<string>(request.Payload, "user_id") ?? "";
        var teamId = GetPayloadValue<string>(request.Payload, "team_id") ?? "";
        var responseUrl = GetPayloadValue<string>(request.Payload, "response_url");

        // Build query based on command
        var query = command switch
        {
            "/dw-search" => $"Find files {text}".Trim(),
            "/dw-status" => "What is my storage status?",
            "/dw-backup" => string.IsNullOrEmpty(text) ? "List my backups" : $"{text} backup",
            _ => string.IsNullOrWhiteSpace(text) ? "help" : text
        };

        var fullUserId = $"slack:{teamId}:{userId}";
        var conversationId = $"slack:{channelId}:{Guid.NewGuid():N}";

        // Route to AI
        var aiResponse = await RouteToIntelligenceAsync(
            "intelligence.request.conversation",
            new Dictionary<string, object>
            {
                ["message"] = query,
                ["platform"] = "slack"
            },
            fullUserId,
            conversationId,
            ct);

        // Build Slack response
        var slackResponse = BuildSlackResponse(aiResponse);

        // Send delayed response if needed
        if (!string.IsNullOrEmpty(responseUrl))
        {
            _ = SendDelayedResponseAsync(responseUrl, aiResponse, ct);
        }

        return ChannelResponse.Success(slackResponse);
    }

    private async Task<ChannelResponse> HandleInteractiveMessageAsync(ChannelRequest request, CancellationToken ct)
    {
        if (!request.Payload.TryGetValue("actions", out var actionsObj) ||
            actionsObj is not JsonElement actionsElement)
        {
            return ChannelResponse.Success();
        }

        var actions = actionsElement.EnumerateArray().ToList();
        if (actions.Count == 0)
        {
            return ChannelResponse.Success();
        }

        var action = actions[0];
        var actionId = action.TryGetProperty("action_id", out var aid) ? aid.GetString() : "";
        var value = action.TryGetProperty("value", out var val) ? val.GetString() : "";

        // Build query from action
        var query = actionId switch
        {
            "search_more" => $"Show more results for {value}",
            "filter_type" => $"Filter by type {value}",
            "show_details" => $"Show details for {value}",
            "create_backup" => "Create a backup",
            "list_backups" => "List my backups",
            _ => value ?? "help"
        };

        var userId = ExtractUserId(request.Payload);
        var channelId = ExtractChannelId(request.Payload);

        var aiResponse = await RouteToIntelligenceAsync(
            "intelligence.request.conversation",
            new Dictionary<string, object>
            {
                ["message"] = query,
                ["platform"] = "slack"
            },
            userId,
            $"slack:{channelId}",
            ct);

        return ChannelResponse.Success(BuildSlackResponse(aiResponse));
    }

    private async Task<ChannelResponse> HandleViewSubmissionAsync(ChannelRequest request, CancellationToken ct)
    {
        // Handle modal form submissions
        return ChannelResponse.Success(new { response_action = "clear" });
    }

    #endregion

    #region Helpers

    private async Task SendSlackMessageAsync(string channel, AICapabilityResponse response, string? threadTs, CancellationToken ct)
    {
        var message = new
        {
            channel,
            thread_ts = threadTs,
            text = response.Response ?? "I processed your request.",
            blocks = BuildMessageBlocks(response)
        };

        var content = new StringContent(
            JsonSerializer.Serialize(message),
            Encoding.UTF8,
            "application/json");

        await _httpClient.PostAsync("https://slack.com/api/chat.postMessage", content, ct);
    }

    private async Task SendDelayedResponseAsync(string responseUrl, AICapabilityResponse response, CancellationToken ct)
    {
        try
        {
            var message = BuildSlackResponse(response, "in_channel");

            var content = new StringContent(
                JsonSerializer.Serialize(message),
                Encoding.UTF8,
                "application/json");

            await _httpClient.PostAsync(responseUrl, content, ct);
        }
        catch
        {
            // Ignore errors in delayed responses
        }
    }

    private object BuildSlackResponse(AICapabilityResponse response, string responseType = "ephemeral")
    {
        return new
        {
            response_type = responseType,
            text = response.Response ?? (response.Success ? "Request processed successfully." : "An error occurred."),
            blocks = BuildMessageBlocks(response)
        };
    }

    private object[]? BuildMessageBlocks(AICapabilityResponse response)
    {
        var blocks = new List<object>
        {
            new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = response.Response ?? "Request processed."
                }
            }
        };

        // Add suggested follow-ups
        if (response.SuggestedFollowUps.Count > 0)
        {
            blocks.Add(new { type = "divider" });
            blocks.Add(new
            {
                type = "context",
                elements = new object[]
                {
                    new
                    {
                        type = "mrkdwn",
                        text = "*Suggested:* " + string.Join(" | ", response.SuggestedFollowUps.Take(3))
                    }
                }
            });
        }

        return blocks.ToArray();
    }

    private string ExtractUserId(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("user", out var userObj) && userObj is JsonElement userEl)
        {
            if (userEl.TryGetProperty("id", out var idProp))
                return $"slack:{idProp.GetString()}";
        }
        return "slack:unknown";
    }

    private string ExtractChannelId(Dictionary<string, object> payload)
    {
        if (payload.TryGetValue("channel", out var chObj) && chObj is JsonElement chEl)
        {
            if (chEl.TryGetProperty("id", out var idProp))
                return idProp.GetString() ?? "";
        }
        return "";
    }

    private T? GetPayloadValue<T>(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value))
            return default;

        if (value is T typed)
            return typed;

        if (value is JsonElement element)
        {
            try { return element.Deserialize<T>(); }
            catch { return default; }
        }

        return default;
    }

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Configuration for Slack channel.
/// </summary>
public sealed class SlackChannelConfig
{
    /// <summary>Slack signing secret for webhook verification.</summary>
    public string SigningSecret { get; init; } = string.Empty;

    /// <summary>Bot OAuth token for API calls.</summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>App-level token for Socket Mode.</summary>
    public string? AppToken { get; init; }

    /// <summary>Bot user ID for mention filtering.</summary>
    public string? BotUserId { get; init; }

    /// <summary>Base URL for webhook endpoints.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>OAuth client ID.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth client secret.</summary>
    public string? ClientSecret { get; init; }
}

/// <summary>
/// Slack event structure.
/// </summary>
internal sealed class SlackEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("user")]
    public string User { get; init; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("ts")]
    public string Timestamp { get; init; } = string.Empty;

    [JsonPropertyName("thread_ts")]
    public string? ThreadTimestamp { get; init; }

    [JsonPropertyName("bot_id")]
    public string? BotId { get; init; }
}
