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
/// Microsoft Teams integration using Bot Framework.
/// Supports messages, adaptive cards, and tab integration.
/// </summary>
public sealed class TeamsIntegration : IntegrationProviderBase, IDisposable
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly TeamsConfig _config;
    private readonly HttpClient _httpClient;
    private string? _accessToken;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public override string PlatformId => "teams";
    public override string PlatformName => "Microsoft Teams";
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.AppId) &&
                                         !string.IsNullOrEmpty(_config.AppPassword);

    public TeamsIntegration(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        TeamsConfig? config = null,
        HttpClient? httpClient = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _config = config ?? new TeamsConfig();
        _httpClient = httpClient ?? new HttpClient();
    }

    public override async Task<IntegrationResponse> HandleWebhookAsync(WebhookEvent webhookEvent, CancellationToken ct = default)
    {
        // Parse the activity
        var activityJson = JsonSerializer.Serialize(webhookEvent.Payload);
        var activity = JsonSerializer.Deserialize<TeamsActivity>(activityJson);

        if (activity == null)
        {
            return ErrorResponse(400, "Invalid activity");
        }

        return activity.Type switch
        {
            "message" => await HandleMessageAsync(activity, ct),
            "conversationUpdate" => await HandleConversationUpdateAsync(activity, ct),
            "invoke" => await HandleInvokeAsync(activity, ct),
            "messageReaction" => SuccessResponse(),
            _ => SuccessResponse()
        };
    }

    public override bool ValidateWebhookSignature(string body, string signature, IDictionary<string, string> headers)
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
            // Parse JWT manually (without external library)
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            // Decode payload (second part)
            var payload = parts[1];
            // Add padding if needed
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            // Replace URL-safe characters
            payload = payload.Replace('-', '+').Replace('_', '/');
            var payloadBytes = Convert.FromBase64String(payload);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);
            var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson);

            if (claims == null)
            {
                return false;
            }

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
            else
            {
                return false;
            }

            // Verify audience (should be our app ID)
            if (claims.TryGetValue("aud", out var audElement))
            {
                var audience = audElement.ValueKind == JsonValueKind.Array
                    ? audElement.EnumerateArray().FirstOrDefault().GetString()
                    : audElement.GetString();

                if (audience != _config.AppId)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            // Check expiration
            if (claims.TryGetValue("exp", out var expElement) && expElement.TryGetInt64(out var expTimestamp))
            {
                var expirationTime = DateTimeOffset.FromUnixTimeSeconds(expTimestamp).UtcDateTime;
                if (expirationTime < DateTime.UtcNow)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public override object GetConfiguration()
    {
        return new
        {
            // Bot manifest for Teams App Studio
            manifestVersion = "1.16",
            version = "1.0.0",
            id = _config.AppId,
            packageName = "com.datawarehouse.teams",
            developer = new
            {
                name = "DataWarehouse",
                websiteUrl = _config.WebsiteUrl,
                privacyUrl = _config.PrivacyUrl,
                termsOfUseUrl = _config.TermsUrl
            },
            name = new
            {
                @short = "DataWarehouse",
                full = "DataWarehouse Assistant"
            },
            description = new
            {
                @short = "Manage your data warehouse from Teams",
                full = "Query files, check storage status, manage backups, and more using natural language commands."
            },
            icons = new
            {
                outline = "outline.png",
                color = "color.png"
            },
            accentColor = "#0066cc",
            bots = new[]
            {
                new
                {
                    botId = _config.AppId,
                    scopes = new[] { "personal", "team", "groupchat" },
                    supportsFiles = false,
                    isNotificationOnly = false,
                    commandLists = new[]
                    {
                        new
                        {
                            scopes = new[] { "personal", "team", "groupchat" },
                            commands = new[]
                            {
                                new { title = "search", description = "Search for files" },
                                new { title = "status", description = "Check storage status" },
                                new { title = "backup", description = "Manage backups" },
                                new { title = "usage", description = "Check storage usage" },
                                new { title = "help", description = "Show available commands" }
                            }
                        }
                    }
                }
            },
            permissions = new[]
            {
                "identity",
                "messageTeamMembers"
            },
            validDomains = new[]
            {
                new Uri(_config.BaseUrl).Host
            },
            webApplicationInfo = new
            {
                id = _config.AppId,
                resource = $"api://{new Uri(_config.BaseUrl).Host}/{_config.AppId}"
            }
        };
    }

    #region Private Methods

    private async Task<IntegrationResponse> HandleMessageAsync(TeamsActivity activity, CancellationToken ct)
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

        // Get or create conversation
        var userId = $"teams:{activity.From?.Id ?? "unknown"}";
        var conversationId = $"teams:{activity.Conversation?.Id ?? Guid.NewGuid().ToString()}";

        var conversation = _conversationEngine.GetConversation(conversationId)
            ?? _conversationEngine.StartConversation(userId, "teams", activity.Conversation?.Id);

        // Process message
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, text, null, ct);

        // Send response
        await SendTeamsResponseAsync(activity, result, ct);

        return SuccessResponse();
    }

    private async Task<IntegrationResponse> HandleConversationUpdateAsync(TeamsActivity activity, CancellationToken ct)
    {
        // Handle when bot is added to a conversation
        if (activity.Value != null || activity.Attachments?.Count > 0)
        {
            // Send welcome message
            var welcomeCard = CreateWelcomeCard();
            await SendAdaptiveCardAsync(activity, welcomeCard, ct);
        }

        return SuccessResponse();
    }

    private async Task<IntegrationResponse> HandleInvokeAsync(TeamsActivity activity, CancellationToken ct)
    {
        // Handle adaptive card actions and other invokes
        var value = activity.Value;

        if (value != null && value.TryGetValue("action", out var actionObj))
        {
            var action = actionObj?.ToString();
            var query = value.TryGetValue("query", out var queryObj) ? queryObj?.ToString() : action;

            if (!string.IsNullOrEmpty(query))
            {
                var userId = $"teams:{activity.From?.Id ?? "unknown"}";
                var conversation = _conversationEngine.StartConversation(userId, "teams");
                var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

                await SendTeamsResponseAsync(activity, result, ct);
            }
        }

        return SuccessResponse(new { status = 200 });
    }

    private async Task SendTeamsResponseAsync(TeamsActivity activity, ConversationTurnResult result, CancellationToken ct)
    {
        await EnsureAccessTokenAsync(ct);

        // Build response with adaptive card if we have structured data
        object response = result.Data != null
            ? new
            {
                type = "message",
                attachments = new[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        content = CreateResponseCard(result)
                    }
                }
            }
            : new
            {
                type = "message",
                text = result.Response
            };

        var url = $"{activity.ServiceUrl}v3/conversations/{activity.Conversation?.Id}/activities/{activity.Id}";

        var content = new StringContent(
            JsonSerializer.Serialize(response),
            Encoding.UTF8,
            "application/json");

        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);

        await _httpClient.PostAsync(url, content, ct);
    }

    private async Task SendAdaptiveCardAsync(TeamsActivity activity, AdaptiveCard card, CancellationToken ct)
    {
        await EnsureAccessTokenAsync(ct);

        var response = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = card
                }
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
            content,
            ct);

        var tokenJson = await response.Content.ReadAsStringAsync(ct);
        var tokenDoc = JsonDocument.Parse(tokenJson);

        _accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = tokenDoc.RootElement.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
    }

    private AdaptiveCard CreateWelcomeCard()
    {
        return new AdaptiveCard
        {
            Body = new List<object>
            {
                new
                {
                    type = "TextBlock",
                    size = "Large",
                    weight = "Bolder",
                    text = "Welcome to DataWarehouse!"
                },
                new
                {
                    type = "TextBlock",
                    text = "I can help you manage your data warehouse. Try these commands:",
                    wrap = true
                },
                new
                {
                    type = "FactSet",
                    facts = new[]
                    {
                        new { title = "search", value = "Search for files" },
                        new { title = "status", value = "Check storage status" },
                        new { title = "backup", value = "Manage backups" },
                        new { title = "usage", value = "Check storage usage" }
                    }
                }
            },
            Actions = new List<object>
            {
                new
                {
                    type = "Action.Submit",
                    title = "Check Status",
                    data = new { action = "status" }
                },
                new
                {
                    type = "Action.Submit",
                    title = "List Backups",
                    data = new { action = "list backups" }
                }
            }
        };
    }

    private AdaptiveCard CreateResponseCard(ConversationTurnResult result)
    {
        var body = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = result.Response,
                wrap = true
            }
        };

        // Add clarification options
        if (result.Clarification != null && result.Clarification.Options.Count > 0)
        {
            body.Add(new
            {
                type = "TextBlock",
                text = result.Clarification.Question,
                weight = "Bolder"
            });
        }

        var actions = new List<object>();

        // Add action buttons for clarifications
        if (result.Clarification != null)
        {
            foreach (var opt in result.Clarification.Options.Take(4))
            {
                actions.Add(new
                {
                    type = "Action.Submit",
                    title = opt.Label,
                    data = new { action = opt.Value, query = opt.Value }
                });
            }
        }

        // Add suggested follow-ups
        foreach (var suggestion in result.SuggestedFollowUps.Take(2))
        {
            actions.Add(new
            {
                type = "Action.Submit",
                title = suggestion.Length > 25 ? suggestion.Substring(0, 22) + "..." : suggestion,
                data = new { action = suggestion, query = suggestion }
            });
        }

        return new AdaptiveCard
        {
            Body = body,
            Actions = actions.Count > 0 ? actions : null
        };
    }

    #endregion

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Configuration for Microsoft Teams integration.
/// </summary>
public sealed class TeamsConfig
{
    /// <summary>Whether Teams integration is enabled.</summary>
    public bool Enabled { get; init; }

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
