// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AIInterface.Channels;

/// <summary>
/// Microsoft Teams integration channel using Bot Framework.
/// Routes Teams requests (messages, adaptive cards, invokes) to AIAgents via message bus.
/// </summary>
/// <remarks>
/// <para>
/// This channel handles:
/// <list type="bullet">
/// <item>Direct messages and channel mentions</item>
/// <item>Adaptive Card actions and submissions</item>
/// <item>Conversation updates (bot added/removed)</item>
/// <item>Invoke activities for dynamic content</item>
/// </list>
/// </para>
/// <para>
/// All AI processing is delegated to the AIAgents plugin. This channel only handles
/// Teams Bot Framework protocol translation and adaptive card formatting.
/// </para>
/// </remarks>
public sealed class TeamsChannel : IntegrationChannelBase, IDisposable
{
    private readonly TeamsChannelConfig _config;
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    /// <inheritdoc />
    public override string ChannelId => "teams";

    /// <inheritdoc />
    public override string ChannelName => "Microsoft Teams";

    /// <inheritdoc />
    public override ChannelCategory Category => ChannelCategory.Chat;

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.AppId) &&
                                         !string.IsNullOrEmpty(_config.AppPassword);

    /// <summary>
    /// Initializes a new instance of the <see cref="TeamsChannel"/> class.
    /// </summary>
    /// <param name="config">Teams configuration.</param>
    /// <param name="httpClient">HTTP client for API calls.</param>
    public TeamsChannel(TeamsChannelConfig? config = null, HttpClient? httpClient = null)
    {
        _config = config ?? new TeamsChannelConfig();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        var activityJson = JsonSerializer.Serialize(request.Payload);
        var activity = JsonSerializer.Deserialize<TeamsActivity>(activityJson);

        if (activity == null)
        {
            return ChannelResponse.Error(400, "Invalid activity");
        }

        return activity.Type switch
        {
            "message" => await HandleMessageAsync(activity, ct),
            "conversationUpdate" => await HandleConversationUpdateAsync(activity, ct),
            "invoke" => await HandleInvokeAsync(activity, ct),
            "messageReaction" => ChannelResponse.Success(),
            _ => ChannelResponse.Success()
        };
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // Teams uses JWT Bearer tokens for authentication
        if (!headers.TryGetValue("Authorization", out var authHeader) ||
            !authHeader.StartsWith("Bearer "))
        {
            return false;
        }

        var token = authHeader.Substring(7);

        try
        {
            // Parse JWT manually
            var parts = token.Split('.');
            if (parts.Length != 3) return false;

            // Decode payload
            var payload = parts[1];
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            payload = payload.Replace('-', '+').Replace('_', '/');
            var payloadBytes = Convert.FromBase64String(payload);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);
            var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);

            if (claims == null) return false;

            // Verify issuer
            if (claims.TryGetValue("iss", out var issElement))
            {
                var issuer = issElement.GetString() ?? "";
                if (!issuer.StartsWith("https://api.botframework.com") &&
                    !issuer.StartsWith("https://sts.windows.net/"))
                {
                    return false;
                }
            }
            else return false;

            // Verify audience
            if (claims.TryGetValue("aud", out var audElement))
            {
                var audience = audElement.ValueKind == JsonValueKind.Array
                    ? audElement.EnumerateArray().FirstOrDefault().GetString()
                    : audElement.GetString();

                if (audience != _config.AppId) return false;
            }
            else return false;

            // Check expiration
            if (claims.TryGetValue("exp", out var expElement) && expElement.TryGetInt64(out var expTimestamp))
            {
                var expirationTime = DateTimeOffset.FromUnixTimeSeconds(expTimestamp).UtcDateTime;
                if (expirationTime < DateTime.UtcNow) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override object GetConfiguration()
    {
        return new
        {
            manifestVersion = "1.16",
            version = "1.0.0",
            id = _config.AppId,
            packageName = "com.datawarehouse.ai.teams",
            developer = new
            {
                name = "DataWarehouse AI",
                websiteUrl = _config.WebsiteUrl,
                privacyUrl = _config.PrivacyUrl,
                termsOfUseUrl = _config.TermsUrl
            },
            name = new { @short = "DataWarehouse AI", full = "DataWarehouse AI Assistant" },
            description = new
            {
                @short = "AI-powered data warehouse management",
                full = "Use AI to query files, check storage status, manage backups, and get intelligent insights about your data warehouse."
            },
            accentColor = "#0066cc",
            bots = new[]
            {
                new
                {
                    botId = _config.AppId,
                    scopes = new[] { "personal", "team", "groupchat" },
                    commandLists = new[]
                    {
                        new
                        {
                            scopes = new[] { "personal", "team", "groupchat" },
                            commands = new[]
                            {
                                new { title = "search", description = "AI-powered file search" },
                                new { title = "status", description = "Get AI storage insights" },
                                new { title = "backup", description = "AI backup management" },
                                new { title = "help", description = "Show AI capabilities" }
                            }
                        }
                    }
                }
            },
            validDomains = !string.IsNullOrEmpty(_config.BaseUrl)
                ? new[] { new Uri(_config.BaseUrl).Host }
                : Array.Empty<string>()
        };
    }

    #region Request Handlers

    private async Task<ChannelResponse> HandleMessageAsync(TeamsActivity activity, CancellationToken ct)
    {
        var text = activity.Text?.Trim() ?? "";

        // Remove @mention of the bot
        if (text.Contains("<at>"))
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<at>[^<]*</at>", "").Trim();
        }

        if (string.IsNullOrEmpty(text))
        {
            text = "help";
        }

        var userId = $"teams:{activity.From?.Id ?? "unknown"}";
        var conversationId = $"teams:{activity.Conversation?.Id ?? Guid.NewGuid().ToString()}";

        // Route to AI
        var aiResponse = await RouteToAIAgentsAsync(
            "ai.chat",
            new Dictionary<string, object>
            {
                ["message"] = text,
                ["platform"] = "teams"
            },
            userId,
            conversationId,
            ct);

        // Send response
        await SendTeamsResponseAsync(activity, aiResponse, ct);

        return ChannelResponse.Success();
    }

    private async Task<ChannelResponse> HandleConversationUpdateAsync(TeamsActivity activity, CancellationToken ct)
    {
        // Send welcome message when bot is added
        if (activity.Value != null || activity.Attachments?.Count > 0)
        {
            var welcomeCard = CreateWelcomeCard();
            await SendAdaptiveCardAsync(activity, welcomeCard, ct);
        }

        return ChannelResponse.Success();
    }

    private async Task<ChannelResponse> HandleInvokeAsync(TeamsActivity activity, CancellationToken ct)
    {
        var value = activity.Value;

        if (value != null && value.TryGetValue("action", out var actionObj))
        {
            var action = actionObj?.ToString();
            var query = value.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : action;

            if (!string.IsNullOrEmpty(query))
            {
                var userId = $"teams:{activity.From?.Id ?? "unknown"}";

                var aiResponse = await RouteToAIAgentsAsync(
                    "ai.chat",
                    new Dictionary<string, object>
                    {
                        ["message"] = query,
                        ["platform"] = "teams"
                    },
                    userId,
                    null,
                    ct);

                await SendTeamsResponseAsync(activity, aiResponse, ct);
            }
        }

        return ChannelResponse.Success(new { status = 200 });
    }

    #endregion

    #region Response Helpers

    private async Task SendTeamsResponseAsync(TeamsActivity activity, AICapabilityResponse response, CancellationToken ct)
    {
        await EnsureAccessTokenAsync(ct);

        object responseMessage = response.Data != null
            ? new
            {
                type = "message",
                attachments = new[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        content = CreateResponseCard(response)
                    }
                }
            }
            : new
            {
                type = "message",
                text = response.Response ?? "Request processed."
            };

        var url = $"{activity.ServiceUrl}v3/conversations/{activity.Conversation?.Id}/activities/{activity.Id}";

        var content = new StringContent(
            JsonSerializer.Serialize(responseMessage),
            Encoding.UTF8,
            "application/json");

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);

        await _httpClient.PostAsync(url, content, ct);
    }

    private async Task SendAdaptiveCardAsync(TeamsActivity activity, object card, CancellationToken ct)
    {
        await EnsureAccessTokenAsync(ct);

        var response = new
        {
            type = "message",
            attachments = new[]
            {
                new { contentType = "application/vnd.microsoft.card.adaptive", content = card }
            }
        };

        var url = $"{activity.ServiceUrl}v3/conversations/{activity.Conversation?.Id}/activities";

        var content = new StringContent(
            JsonSerializer.Serialize(response),
            Encoding.UTF8,
            "application/json");

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);

        await _httpClient.PostAsync(url, content, ct);
    }

    private async Task EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && _tokenExpiry > DateTime.UtcNow.AddMinutes(5))
        {
            return;
        }

        var tokenRequest = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _config.AppId,
            ["client_secret"] = _config.AppPassword,
            ["scope"] = "https://api.botframework.com/.default"
        };

        var content = new FormUrlEncodedContent(tokenRequest);
        var response = await _httpClient.PostAsync(
            "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token",
            content, ct);

        var tokenJson = await response.Content.ReadAsStringAsync(ct);
        var tokenDoc = JsonDocument.Parse(tokenJson);

        _accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = tokenDoc.RootElement.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
    }

    private object CreateWelcomeCard()
    {
        return new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body = new object[]
            {
                new { type = "TextBlock", size = "Large", weight = "Bolder", text = "Welcome to DataWarehouse AI!" },
                new { type = "TextBlock", text = "I can help you with AI-powered data management. Try these commands:", wrap = true },
                new
                {
                    type = "FactSet",
                    facts = new[]
                    {
                        new { title = "search", value = "AI-powered file search" },
                        new { title = "status", value = "Get AI storage insights" },
                        new { title = "backup", value = "AI backup management" }
                    }
                }
            },
            actions = new object[]
            {
                new { type = "Action.Submit", title = "Check Status", data = new { action = "status" } },
                new { type = "Action.Submit", title = "List Backups", data = new { action = "list backups" } }
            }
        };
    }

    private object CreateResponseCard(AICapabilityResponse response)
    {
        var body = new List<object>
        {
            new { type = "TextBlock", text = response.Response ?? "Request processed.", wrap = true }
        };

        var actions = new List<object>();

        foreach (var suggestion in response.SuggestedFollowUps.Take(3))
        {
            actions.Add(new
            {
                type = "Action.Submit",
                title = suggestion.Length > 25 ? suggestion.Substring(0, 22) + "..." : suggestion,
                data = new { action = suggestion, query = suggestion }
            });
        }

        return new
        {
            type = "AdaptiveCard",
            version = "1.4",
            body,
            actions = actions.Count > 0 ? actions.ToArray() : null
        };
    }

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Configuration for Teams channel.
/// </summary>
public sealed class TeamsChannelConfig
{
    /// <summary>Bot Framework App ID.</summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>Bot Framework App Password.</summary>
    public string AppPassword { get; init; } = string.Empty;

    /// <summary>Base URL for webhooks.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Website URL.</summary>
    public string? WebsiteUrl { get; init; }

    /// <summary>Privacy policy URL.</summary>
    public string? PrivacyUrl { get; init; }

    /// <summary>Terms of service URL.</summary>
    public string? TermsUrl { get; init; }

    /// <summary>Tenant ID for single-tenant apps.</summary>
    public string? TenantId { get; init; }
}

/// <summary>
/// Teams activity structure.
/// </summary>
internal sealed class TeamsActivity
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("serviceUrl")]
    public string ServiceUrl { get; init; } = string.Empty;

    [JsonPropertyName("from")]
    public TeamsAccount? From { get; init; }

    [JsonPropertyName("conversation")]
    public TeamsConversation? Conversation { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("value")]
    public Dictionary<string, object>? Value { get; init; }

    [JsonPropertyName("attachments")]
    public List<object>? Attachments { get; init; }
}

internal sealed class TeamsAccount
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed class TeamsConversation
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("conversationType")]
    public string? ConversationType { get; init; }
}
