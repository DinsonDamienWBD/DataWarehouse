// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Integrations;

/// <summary>
/// Slack integration provider.
/// Supports slash commands, interactive messages, event subscriptions, and OAuth.
/// </summary>
public sealed class SlackIntegration : IntegrationProviderBase, IDisposable
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly SlackConfig _config;
    private readonly HttpClient _httpClient;

    public override string PlatformId => "slack";
    public override string PlatformName => "Slack";
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.SigningSecret) &&
                                         !string.IsNullOrEmpty(_config.BotToken);

    public SlackIntegration(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        SlackConfig? config = null,
        HttpClient? httpClient = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _config = config ?? new SlackConfig();
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.BotToken);
    }

    public override async Task<IntegrationResponse> HandleWebhookAsync(WebhookEvent webhookEvent, CancellationToken ct = default)
    {
        var eventType = webhookEvent.Type;

        return eventType switch
        {
            "url_verification" => HandleUrlVerification(webhookEvent),
            "event_callback" => await HandleEventCallbackAsync(webhookEvent, ct),
            "slash_command" => await HandleSlashCommandAsync(webhookEvent, ct),
            "interactive_message" or "block_actions" => await HandleInteractiveMessageAsync(webhookEvent, ct),
            "view_submission" => await HandleViewSubmissionAsync(webhookEvent, ct),
            _ => SuccessResponse(new { ok = true })
        };
    }

    public override bool ValidateWebhookSignature(string body, string signature, IDictionary<string, string> headers)
    {
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

    public override object GetConfiguration()
    {
        return new
        {
            app = new
            {
                display_name = "DataWarehouse",
                description = "Manage your data warehouse from Slack",
                background_color = "#0066cc"
            },
            features = new
            {
                bot_user = new
                {
                    display_name = "DataWarehouse",
                    always_online = true
                },
                slash_commands = new object[]
                {
                    new
                    {
                        command = "/dw",
                        url = $"{_config.BaseUrl}/api/integrations/slack/slash",
                        description = "Query your DataWarehouse",
                        usage_hint = "[search query or command]",
                        should_escape = false
                    },
                    new
                    {
                        command = "/dw-search",
                        url = $"{_config.BaseUrl}/api/integrations/slack/slash",
                        description = "Search files in DataWarehouse",
                        usage_hint = "[search query]",
                        should_escape = false
                    },
                    new
                    {
                        command = "/dw-status",
                        url = $"{_config.BaseUrl}/api/integrations/slack/slash",
                        description = "Check DataWarehouse status",
                        should_escape = false
                    },
                    new
                    {
                        command = "/dw-backup",
                        url = $"{_config.BaseUrl}/api/integrations/slack/slash",
                        description = "Manage backups",
                        usage_hint = "[create|list|verify]",
                        should_escape = false
                    }
                },
                interactivity = new
                {
                    is_enabled = true,
                    request_url = $"{_config.BaseUrl}/api/integrations/slack/interactive",
                    message_menu_options_url = $"{_config.BaseUrl}/api/integrations/slack/options"
                }
            },
            oauth_config = new
            {
                redirect_urls = new[] { $"{_config.BaseUrl}/api/integrations/slack/oauth/callback" },
                scopes = new
                {
                    bot = new[]
                    {
                        "app_mentions:read",
                        "channels:history",
                        "chat:write",
                        "commands",
                        "groups:history",
                        "im:history",
                        "im:read",
                        "im:write",
                        "users:read"
                    }
                }
            },
            settings = new
            {
                event_subscriptions = new
                {
                    request_url = $"{_config.BaseUrl}/api/integrations/slack/events",
                    bot_events = new[]
                    {
                        "app_mention",
                        "message.im",
                        "message.groups",
                        "message.channels"
                    }
                },
                interactivity = new
                {
                    is_enabled = true,
                    request_url = $"{_config.BaseUrl}/api/integrations/slack/interactive"
                },
                org_deploy_enabled = false,
                socket_mode_enabled = false
            }
        };
    }

    #region Private Methods

    private IntegrationResponse HandleUrlVerification(WebhookEvent webhookEvent)
    {
        if (webhookEvent.Payload.TryGetValue("challenge", out var challenge))
        {
            return VerificationResponse(challenge?.ToString() ?? "");
        }
        return ErrorResponse(400, "Missing challenge");
    }

    private async Task<IntegrationResponse> HandleEventCallbackAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        // Parse event
        if (!webhookEvent.Payload.TryGetValue("event", out var eventObj))
        {
            return SuccessResponse();
        }

        var eventJson = JsonSerializer.Serialize(eventObj);
        var slackEvent = JsonSerializer.Deserialize<SlackEvent>(eventJson);

        if (slackEvent == null || !string.IsNullOrEmpty(slackEvent.BotId))
        {
            // Ignore bot messages to prevent loops
            return SuccessResponse();
        }

        // Handle based on event type
        switch (slackEvent.Type)
        {
            case "app_mention":
            case "message":
                await HandleMessageEventAsync(slackEvent, webhookEvent.Payload, ct);
                break;
        }

        return SuccessResponse();
    }

    private async Task HandleMessageEventAsync(SlackEvent slackEvent, Dictionary<string, object> payload, CancellationToken ct)
    {
        var teamId = payload.TryGetValue("team_id", out var tid) ? tid?.ToString() : "unknown";
        var userId = $"slack:{teamId}:{slackEvent.User}";

        // Get or create conversation
        var conversationId = $"slack:{slackEvent.Channel}:{slackEvent.ThreadTimestamp ?? slackEvent.Timestamp}";
        var conversation = _conversationEngine.GetConversation(conversationId)
            ?? _conversationEngine.StartConversation(userId, "slack", slackEvent.Channel);

        // Process the message
        var text = slackEvent.Text.Replace($"<@{_config.BotUserId}>", "").Trim();
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, text, null, ct);

        // Send response
        await SendSlackMessageAsync(slackEvent.Channel, result, slackEvent.ThreadTimestamp ?? slackEvent.Timestamp, ct);
    }

    private async Task<IntegrationResponse> HandleSlashCommandAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var command = webhookEvent.Payload.TryGetValue("command", out var cmd) ? cmd?.ToString() : "/dw";
        var text = webhookEvent.Payload.TryGetValue("text", out var txt) ? txt?.ToString() ?? "" : "";
        var channelId = webhookEvent.Payload.TryGetValue("channel_id", out var ch) ? ch?.ToString() ?? "" : "";
        var userId = webhookEvent.Payload.TryGetValue("user_id", out var uid) ? uid?.ToString() ?? "" : "";
        var teamId = webhookEvent.Payload.TryGetValue("team_id", out var tid) ? tid?.ToString() ?? "" : "";
        var responseUrl = webhookEvent.Payload.TryGetValue("response_url", out var rurl) ? rurl?.ToString() : null;

        // Build query based on command
        var query = command switch
        {
            "/dw-search" => $"Find files {text}".Trim(),
            "/dw-status" => "What is my storage status?",
            "/dw-backup" => string.IsNullOrEmpty(text) ? "List my backups" : $"{text} backup",
            _ => text
        };

        if (string.IsNullOrWhiteSpace(query))
        {
            query = "help";
        }

        // Get or create conversation
        var fullUserId = $"slack:{teamId}:{userId}";
        var conversation = _conversationEngine.StartConversation(fullUserId, "slack", channelId);

        // Process
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        // Build Slack response
        var response = BuildSlackResponse(result);

        // If we have a response URL, we can send a more detailed response later
        if (!string.IsNullOrEmpty(responseUrl) && result.Data != null)
        {
            // Send immediate acknowledgment
            _ = SendDelayedResponseAsync(responseUrl, result, ct);
        }

        return SuccessResponse(response);
    }

    private async Task<IntegrationResponse> HandleInteractiveMessageAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        if (!webhookEvent.Payload.TryGetValue("actions", out var actionsObj) ||
            actionsObj is not JsonElement actionsElement)
        {
            return SuccessResponse();
        }

        var actions = actionsElement.EnumerateArray().ToList();
        if (actions.Count == 0)
        {
            return SuccessResponse();
        }

        var action = actions[0];
        var actionId = action.TryGetProperty("action_id", out var aid) ? aid.GetString() : "";
        var value = action.TryGetProperty("value", out var val) ? val.GetString() : "";

        var userId = webhookEvent.Payload.TryGetValue("user", out var userObj) &&
                     userObj is JsonElement userEl &&
                     userEl.TryGetProperty("id", out var uidProp)
            ? uidProp.GetString() ?? ""
            : "";

        var channelId = webhookEvent.Payload.TryGetValue("channel", out var chObj) &&
                       chObj is JsonElement chEl &&
                       chEl.TryGetProperty("id", out var chidProp)
            ? chidProp.GetString() ?? ""
            : "";

        // Handle action
        var query = actionId switch
        {
            "search_more" => $"Show more results for {value}",
            "filter_type" => $"Filter by type {value}",
            "show_details" => $"Show details for {value}",
            "create_backup" => "Create a backup",
            "list_backups" => "List my backups",
            _ => value ?? "help"
        };

        var conversation = _conversationEngine.StartConversation($"slack:{userId}", "slack", channelId);
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return SuccessResponse(BuildSlackResponse(result));
    }

    private async Task<IntegrationResponse> HandleViewSubmissionAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        // Handle modal form submissions
        // Extract values from view state and process

        return SuccessResponse(new { response_action = "clear" });
    }

    private async Task SendSlackMessageAsync(string channel, ConversationTurnResult result, string? threadTs, CancellationToken ct)
    {
        var message = new
        {
            channel,
            thread_ts = threadTs,
            text = result.Response,
            blocks = BuildMessageBlocks(result)
        };

        var content = new StringContent(
            JsonSerializer.Serialize(message),
            Encoding.UTF8,
            "application/json");

        await _httpClient.PostAsync("https://slack.com/api/chat.postMessage", content, ct);
    }

    private async Task SendDelayedResponseAsync(string responseUrl, ConversationTurnResult result, CancellationToken ct)
    {
        try
        {
            var message = BuildSlackResponse(result, "in_channel");

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

    private SlackMessageResponse BuildSlackResponse(ConversationTurnResult result, string responseType = "ephemeral")
    {
        return new SlackMessageResponse
        {
            ResponseType = responseType,
            Text = result.Response,
            Blocks = BuildMessageBlocks(result)
        };
    }

    private List<SlackBlock>? BuildMessageBlocks(ConversationTurnResult result)
    {
        var blocks = new List<SlackBlock>
        {
            new()
            {
                Type = "section",
                Text = new SlackTextObject
                {
                    Type = "mrkdwn",
                    Text = result.Response
                }
            }
        };

        // Add clarification options
        if (result.Clarification != null && result.Clarification.Options.Count > 0)
        {
            blocks.Add(new SlackBlock
            {
                Type = "actions",
                Elements = result.Clarification.Options.Select(opt => (object)new
                {
                    type = "button",
                    text = new { type = "plain_text", text = opt.Label },
                    value = opt.Value,
                    action_id = $"clarification_{opt.Value}"
                }).ToList()
            });
        }

        // Add suggested follow-ups
        if (result.SuggestedFollowUps.Count > 0)
        {
            blocks.Add(new SlackBlock { Type = "divider" });
            blocks.Add(new SlackBlock
            {
                Type = "context",
                Elements = new List<object>
                {
                    new SlackTextObject
                    {
                        Type = "mrkdwn",
                        Text = "*Suggested:* " + string.Join(" | ", result.SuggestedFollowUps.Take(3))
                    }
                }
            });
        }

        return blocks;
    }

    #endregion

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Configuration for Slack integration.
/// </summary>
public sealed class SlackConfig
{
    /// <summary>Whether Slack integration is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Slack signing secret.</summary>
    public string SigningSecret { get; init; } = string.Empty;

    /// <summary>Bot OAuth token.</summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>App-level token for Socket Mode.</summary>
    public string? AppToken { get; init; }

    /// <summary>Client ID for OAuth.</summary>
    public string? ClientId { get; init; }

    /// <summary>Client secret for OAuth.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Bot user ID.</summary>
    public string? BotUserId { get; init; }

    /// <summary>Base URL for webhooks.</summary>
    public string BaseUrl { get; init; } = string.Empty;
}
